 // Mechanical storage, provenance, custody and physical comparison only.
 // The graph accepts authority and requests, publishes and dispatches policy.
 bool additionalMode;
 string AdditionalToken(JsonElement q)=>"journal:"+Required(q,"integrationIdentity")+":"+DigestText(Required(q,"integrationRequest"));
 void AdditionalChildGuard(){
  string path=StoragePath("additional/v1/intent");if(!System.IO.File.Exists(path))return;
  // The same pinned three-stage verifier is reused with an explicit namespace.
  EpochChildGuardFor("additional/v1/intent","physical-epoch-child/v1/");
 }
 public NyStr AdditionalFacts(){try{return InspectAdditional();}catch(Exception error){Observe(new {operation="additional-inspection-rejected",reason=error.Message});return "{unavailable}";}}
 string InspectAdditional(){
  EpochOwnerPath(); // Before storage or physical inspection, even on refusal.
  Check(additionalMode&&Setting("evidence")!="unavailable"&&Setting("physicalControl")==""&&Setting("worktreeModeControl")=="","enabled additional inspection required");
  string receiptRaw=System.IO.File.ReadAllText(StoragePath(EpochDispositionReceipt)),requestRaw=System.IO.File.ReadAllText(StoragePath(EpochDispositionIntent));
  using var receiptDoc=JsonDocument.Parse(receiptRaw);var r=receiptDoc.RootElement;
  using var decisionDoc=JsonDocument.Parse(requestRaw);var disposition=decisionDoc.RootElement;
  Check(Required(r,"request")==requestRaw&&Required(r,"requestDigest")==DigestText(requestRaw),"complete predecessor receipt required");
  foreach(string name in new[]{"originalOutcome","continuationOutcome","freshOutcome"})Check(Required(r,name)=="unknown","three retained unknowns required");
  Check(Required(r,"admission")=="closed"&&Required(r,"newEffectState")=="applied","closed applied predecessor required");
  string acceptedRaw=Required(r,"epochRequest");using var acceptedDoc=JsonDocument.Parse(acceptedRaw);var accepted=acceptedDoc.RootElement;
  Check(acceptedRaw==System.IO.File.ReadAllText(StoragePath("epoch/v1/intent")),"accepted epoch bytes changed");
  using var inventoryDoc=JsonDocument.Parse(Required(r,"inventory"));var inventory=inventoryDoc.RootElement;
  foreach(var item in inventory.EnumerateObject()){
   Check(System.Text.RegularExpressions.Regex.IsMatch(item.Name,@"\A[0-9a-f]{64}\.json\z"),"frozen inventory name");
   Check(DigestText(System.IO.File.ReadAllText(System.IO.Path.Combine(StorageRoot,item.Name)))==item.Value.GetString(),"frozen evidence changed");
  }
  // Retained original/continuation launches remain terminal. Late witnesses do
  // not remove an active launch fence, and no old owner/provider is restarted.
  var original=JsonSerializer.Deserialize<GitChild>(System.IO.File.ReadAllText(StoragePath("physical-child/v1")))!;
  Check(original.Version=="2"&&original.OwnerDigest==SupportedContinuationOwner&&original.State=="quiescent"&&original.Pid.HasValue&&original.ExitCode==0&&original.InputClosed&&original.OutputDrained,"original quiescent pinned custody required");
  var originalActive=JsonSerializer.Deserialize<GitChild>(System.IO.File.ReadAllText(StoragePath("physical-child/v1")))!;originalActive.State="active";originalActive.ExitCode=null;originalActive.InputClosed=false;originalActive.OutputDrained=false;
  var originalJoin=LoadJoin(originalActive,JsonSerializer.Serialize(originalActive));Check(originalJoin.ExitCode==0&&originalJoin.ExitSignal=="","original terminal witness required");
  var continuation=JsonSerializer.Deserialize<ContinuationIntent>(System.IO.File.ReadAllText(StoragePath(ContinuationKey)))!;
  var oldCustody=InterruptedCustody(continuation,out var last);Check(last!=null,"continuation custody required");
  for(int stage=0;stage<2;stage++){var child=JsonSerializer.Deserialize<GitChild>(System.IO.File.ReadAllText(StoragePath("physical-continuation-child/v1/"+continuation.Id+"/"+stage)))!;Check(child.State=="quiescent"&&child.Pid.HasValue&&child.OwnerDigest==SupportedContinuationOwner&&child.ExitCode==0&&child.InputClosed&&child.OutputDrained,"continuation quiescent custody required");}
  EpochChildGuard();string oldToken=AdditionalToken(accepted);var oldPlan=LoadPlan(oldToken);
  var custody=new List<object>();for(int stage=0;stage<3;stage++){
   string key="physical-epoch-child/v1/"+DigestText(oldToken)+"/"+stage,launch=System.IO.File.ReadAllText(StoragePath(key));var child=JsonSerializer.Deserialize<GitChild>(launch)!;string witness=System.IO.File.ReadAllText(StoragePath("physical-join/v1/"+child.Invocation));custody.Add(new {key,stage,launch,witness,launchDigest=DigestText(launch),witnessDigest=DigestText(witness)});
  }
  string custodyRaw=JsonSerializer.Serialize(custody,new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});Check(custodyRaw==Required(r,"custody"),"frozen epoch custody changed");
  string proposalRaw=Setting("additionalRequest")!=""?System.IO.File.ReadAllText(Setting("additionalRequest")):System.IO.File.Exists(StoragePath("additional/v1/intent"))?System.IO.File.ReadAllText(StoragePath("additional/v1/intent")):"{}";
  using var proposalDoc=JsonDocument.Parse(proposalRaw);var q=proposalDoc.RootElement;
  string token=q.TryGetProperty("integrationIdentity",out _)?AdditionalToken(q):"",memberIdentity=q.TryGetProperty("memberIdentity",out var mid)?mid.GetString()??"":"";
  if(token!="")Check(System.Text.RegularExpressions.Regex.IsMatch(Required(q,"integrationIdentity"),@"\A[0-9a-f]{64}\z")&&System.Text.RegularExpressions.Regex.IsMatch(memberIdentity,@"\A[0-9a-f]{64}\z"),"bounded additional identities");
  bool newPlan=token!=""&&System.IO.File.Exists(StoragePath(PlanKey(token)));var physical=PhysicalState();GitState baseline=physical;
  if(newPlan){AdditionalChildGuard();var plan=LoadPlan(token);baseline=plan.Before;Check(!plan.NoMutation&&plan.Selected==Required(q,"selectedCommit"),"additional plan selection");string state=Corroborated(plan,physical);Check(state=="absent"||state=="applied","additional physical state unresolved");}
  Check(DigestText(JsonSerializer.Serialize(baseline))==Required(r,"currentStateDigest")&&Corroborated(oldPlan,baseline)=="applied","predecessor baseline no longer exact applied state");
  var allowed=inventory.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal);foreach(string k in new[]{EpochDispositionIntent,EpochDispositionReceipt})allowed.Add(System.IO.Path.GetFileName(StoragePath(k)));
  string next=Required(r,"next");
  if(token!=""){
   if(!System.IO.File.Exists(StoragePath("additional/v1/activation"))){foreach(string k in new[]{"additional-journal/v1/"+Required(q,"integrationIdentity"),"additional-member/v1/"+next,"additional-admission/v1/"+memberIdentity,"outcome/"+token,"outcome-receipt/"+token,"physical-receipt/"+token+"/commit","physical-receipt/"+token+"/head","physical-receipt/"+token+"/save"})Check(!System.IO.File.Exists(StoragePath(k))&&!System.IO.File.Exists(StoragePath("additional-lineage/v1/"+DigestText(k))),"application evidence before additional activation");Check(!newPlan,"physical plan before additional activation");}
   // A physical plan is durable before any integration journal/result/receipt
   // or its graph lineage can be published. Orphans cannot become replay facts.
   if(!newPlan)foreach(string k in new[]{"additional-journal/v1/"+Required(q,"integrationIdentity"),"outcome/"+token,"outcome-receipt/"+token,"physical-receipt/"+token+"/commit","physical-receipt/"+token+"/head","physical-receipt/"+token+"/save"})Check(!System.IO.File.Exists(StoragePath(k))&&!System.IO.File.Exists(StoragePath("additional-lineage/v1/"+DigestText(k))),"integration evidence lacks predecessor physical plan");
   string[] admin={"additional/v1/intent","additional/v1/blocked","additional/v1/result","additional/v1/activation"};
   string[] app={"additional-registry/v1","additional-journal/v1/"+Required(q,"integrationIdentity"),"additional-member/v1/"+next,"additional-admission/v1/"+memberIdentity,"outcome/"+token,"outcome-receipt/"+token};
   foreach(string k in admin.Concat(app).Concat(new[]{PlanKey(token),"physical-receipt/"+token+"/commit","physical-receipt/"+token+"/head","physical-receipt/"+token+"/save"}))allowed.Add(System.IO.Path.GetFileName(StoragePath(k)));
   foreach(string k in app){string lineage="additional-lineage/v1/"+DigestText(k);allowed.Add(System.IO.Path.GetFileName(StoragePath(lineage)));if(System.IO.File.Exists(StoragePath("additional/v1/activation"))&&(System.IO.File.Exists(StoragePath(k))||System.IO.File.Exists(StoragePath(lineage))))Check(System.IO.File.ReadAllText(StoragePath(lineage))==System.IO.File.ReadAllText(StoragePath("additional/v1/activation")),"additional application lineage changed");}
   for(int stage=0;stage<3;stage++){string k="physical-epoch-child/v1/"+DigestText(token)+"/"+stage;allowed.Add(System.IO.Path.GetFileName(StoragePath(k)));if(System.IO.File.Exists(StoragePath(k))){var child=JsonSerializer.Deserialize<GitChild>(System.IO.File.ReadAllText(StoragePath(k)))!;allowed.Add(System.IO.Path.GetFileName(StoragePath("physical-join/v1/"+child.Invocation)));}}
   if(!System.IO.File.Exists(StoragePath("additional/v1/intent"))){Check(!newPlan,"orphan additional plan");for(int stage=0;stage<3;stage++)Check(!System.IO.File.Exists(StoragePath("physical-epoch-child/v1/"+DigestText(token)+"/"+stage)),"orphan additional launch");}
   if(System.IO.File.Exists(StoragePath("additional-registry/v1"))){using var currentRegistry=JsonDocument.Parse(System.IO.File.ReadAllText(StoragePath("additional-registry/v1")));if(Required(currentRegistry.RootElement,"phase")!="idle")Check(newPlan,"pending/actual additional registry lacks physical plan");}
   if(newPlan){using var regDoc=JsonDocument.Parse(System.IO.File.ReadAllText(StoragePath("additional-registry/v1")));var reg=regDoc.RootElement;if(Required(reg,"phase")=="ready"){var plan=LoadPlan(token);Check(Required(reg,"intent")==plan.Intent&&Required(reg,"token")==token&&Corroborated(plan,physical)=="applied","ready result physical correlation");var entry=reg.GetProperty("entries").EnumerateArray().Single(x=>Required(x,"id")==next);Check(Required(entry,"integrated")==plan.Commit,"ready Member actual HEAD correlation");}}
  }
  Check(System.IO.Directory.GetFiles(StorageRoot).All(x=>allowed.Contains(System.IO.Path.GetFileName(x))),"unowned additional storage appeared");
  // Candidate provenance comes only from the original immutable object inventory.
  var continuationPlan=LoadPlan(continuation.Identity);string selected=q.TryGetProperty("selectedCommit",out var choice)?choice.GetString()??"":"";
  if(selected!="")Check(continuationPlan.Before.Objects.Split('\n').Contains(selected+" commit")&&Git(new[]{"rev-parse",selected+"^{tree}"})!="","additional candidate lacks frozen owned provenance");
  using var epochFactsDoc=JsonDocument.Parse(Required(r,"epochFacts"));var prior=epochFactsDoc.RootElement.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.Clone(),StringComparer.Ordinal);
  void AddPrior(string k,string v)=>prior[k]=JsonSerializer.SerializeToElement(v);
  foreach(var item in r.EnumerateObject())if(item.Value.ValueKind==JsonValueKind.String)AddPrior(item.Name,item.Value.GetString()!);
  foreach(var item in disposition.EnumerateObject())if(item.Value.ValueKind==JsonValueKind.String&&!new[]{"id","authorization","action","scope"}.Contains(item.Name))AddPrior(item.Name,item.Value.GetString()!);
  AddPrior("inventoryDigest",DigestText(Required(r,"inventory")));AddPrior("custodyDigest",DigestText(custodyRaw));AddPrior("allNewSlots","quiescent");
  foreach(var pair in new[]{("blockedDigest",Required(r,"blocked")),("registryDigest",Required(r,"registry")),("journalDigest",Required(r,"journal")),("planDigest",Required(r,"plan"))})AddPrior(pair.Item1,DigestText(pair.Item2));
  string dispositionAuthority=AuthorityValue(disposition,"authorization"),epochAuthority=AuthorityValue(accepted,"authorization");
  foreach(var pair in new[]{("epochDispositionAuthorization",dispositionAuthority),("epochAuthorization",epochAuthority),("dispositionAuthorization",Required(epochFactsDoc.RootElement,"retentionAuthorization")),("continuationAuthorization",Required(epochFactsDoc.RootElement,"continuationAuthorization")),("interruptedContinuationAuthorization",Required(epochFactsDoc.RootElement,"interruptionAuthorization"))}){string supplied=ScenarioAuthority(pair.Item1,true);Check(supplied==""||supplied==pair.Item2,"ancestor authority label contradicts accepted bytes");}
  var reserved=new List<string>{Required(disposition,"id"),Required(accepted,"id"),Required(accepted,"epoch"),Required(r,"effectIdentity"),Required(accepted,"memberIdentity"),Required(accepted,"integrationIdentity"),Required(epochFactsDoc.RootElement,"retentionId"),Required(epochFactsDoc.RootElement,"continuationId"),Required(epochFactsDoc.RootElement,"interruptionId"),Required(epochFactsDoc.RootElement,"originalIdentity")};
  foreach(string name in new[]{"originalRequest","memberRequest","integrationRequest"}){using var frame=JsonDocument.Parse(Required(accepted,name));var id=frame.RootElement.GetProperty("id");reserved.Add(id.ValueKind==JsonValueKind.String?id.GetString()!:id.GetRawText());}
  return JsonSerializer.Serialize(new {predecessor=receiptRaw,predecessorRequest=requestRaw,priorFacts=JsonSerializer.Serialize(prior),sourceRegistry=Required(r,"registry"),originalBlocked=Required(r,"blocked"),continuationRequest=Required(epochFactsDoc.RootElement,"continuationRequest"),freshRequest=Required(accepted,"integrationRequest"),originalRequest=Required(accepted,"originalRequest"),predecessorDigest=DigestText(receiptRaw),inventoryDigest=DigestText(Required(r,"inventory")),custodyDigest=DigestText(custodyRaw),baselineStateDigest=Required(r,"currentStateDigest"),baselineHead=oldPlan.Commit,repository=GitRoot,repositoryId=Setting("fixtureId"),next,selectedCommit=selected,newPlan=newPlan?"true":"false",originalIdentity=Required(epochFactsDoc.RootElement,"originalIdentity"),reservedIds=reserved,wireFrames=new[]{Required(accepted,"originalRequest"),Required(accepted,"memberRequest"),Required(accepted,"integrationRequest")},authorities=new[]{dispositionAuthority,epochAuthority,Required(epochFactsDoc.RootElement,"retentionAuthorization"),Required(epochFactsDoc.RootElement,"continuationAuthorization"),Required(epochFactsDoc.RootElement,"interruptionAuthorization")}});
 }
