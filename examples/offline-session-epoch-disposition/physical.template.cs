 // Mechanical raw evidence for one fully witnessed current epoch tail.
 // All acceptance, pending-result policy and publication live in the graph.
 const string EpochDispositionIntent="epoch-disposition/v1/intent",EpochDispositionReceipt="epoch-disposition/v1/receipt";
 public NyStr EpochDispositionFacts(){
  try{
   EpochOwnerPath(); // Pin before any storage access or physical inspection.
   string prior=EpochFacts();using var priorDoc=JsonDocument.Parse(prior);var facts=priorDoc.RootElement;
   string accepted=System.IO.File.ReadAllText(StoragePath("epoch/v1/intent"));using var proposal=JsonDocument.Parse(accepted);var q=proposal.RootElement;
   string integration=Required(q,"integrationRequest"),identity=Required(q,"integrationIdentity"),token="journal:"+identity+":"+DigestText(integration);
   var custody=new List<object>();
   for(int stage=0;stage<3;stage++){
    string key="physical-epoch-child/v1/"+DigestText(token)+"/"+stage;
    string launch=System.IO.File.ReadAllText(StoragePath(key));var child=JsonSerializer.Deserialize<GitChild>(launch)!;
    Check(child.State=="quiescent"&&child.Pid.HasValue,"complete quiescent epoch tail required");
    string witness=System.IO.File.ReadAllText(StoragePath("physical-join/v1/"+child.Invocation));
    custody.Add(new {key,stage,launch,witness,launchDigest=DigestText(launch),witnessDigest=DigestText(witness)});
   }
   EpochChildGuard(); // Exact arguments, input, pinned owner and terminal captures.
   string planRaw=System.IO.File.ReadAllText(StoragePath(PlanKey(token)));var plan=LoadPlan(token);
   var physical=PhysicalState();string newEffectState=Corroborated(plan,physical);
   Check(!plan.NoMutation,"fresh mutation plan required");
   string registryRaw=System.IO.File.ReadAllText(StoragePath("epoch-registry/v1")),journalRaw=System.IO.File.ReadAllText(StoragePath("epoch-journal/v1/"+identity));
   using var registryDoc=JsonDocument.Parse(registryRaw);var registry=registryDoc.RootElement;
   Check(Required(registry,"intent")==plan.Intent,"current registry/physical plan correlation");
   using var intentDoc=JsonDocument.Parse(plan.Intent);var intent=intentDoc.RootElement;
   string member=intent.GetProperty("target").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
   var entry=registry.GetProperty("entries").EnumerateArray().Single(x=>Required(x,"id")==member);
   Check(Required(entry,"base")==Required(q,"baselineHead")&&Required(entry,"commit")==plan.Selected&&Required(entry,"integrated")=="","current Member physical selection correlation");
   // Complete current inventory excludes only this separate decision's two files.
   // Sorting fixes receipt bytes and exact replay, never repairs old evidence.
   var inventory=new SortedDictionary<string,string>(StringComparer.Ordinal);
   foreach(string path in System.IO.Directory.GetFiles(StorageRoot)){
    if(path==StoragePath(EpochDispositionIntent)||path==StoragePath(EpochDispositionReceipt))continue;
    inventory.Add(System.IO.Path.GetFileName(path),DigestText(System.IO.File.ReadAllText(path)));
   }
   string inventoryRaw=JsonSerializer.Serialize(inventory),custodyRaw=JsonSerializer.Serialize(custody,new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
   var output=facts.EnumerateObject().ToDictionary(x=>x.Name,x=>x.Value.Clone(),StringComparer.Ordinal);
   void Add(string key,string value)=>output[key]=JsonSerializer.SerializeToElement(value);
   foreach(var pair in new[]{("epochRequest",accepted),("activation",System.IO.File.ReadAllText(StoragePath("epoch/v1/activation"))),("blocked",System.IO.File.ReadAllText(StoragePath("epoch/v1/blocked"))),("originalRegistry",System.IO.File.ReadAllText(StoragePath("registry/v1"))),("registry",registryRaw),("journal",journalRaw),("plan",planRaw),("custody",custodyRaw),("inventory",inventoryRaw),("epochFacts",prior),("integrationRequest",integration),("integrationIdentity",identity),("integrationTypedId",JsonDocument.Parse(integration).RootElement.GetProperty("id").GetRawText()),("effectIdentity",token),("member",member),("next",Required(registry,"next")),("fingerprint",Required(registry,"fingerprint")),("intent",plan.Intent),("newEffectState",newEffectState),("currentStateDigest",DigestText(JsonSerializer.Serialize(physical))),("allNewSlots","quiescent"),("blockedDigest",DigestText(System.IO.File.ReadAllText(StoragePath("epoch/v1/blocked")))),("registryDigest",DigestText(registryRaw)),("journalDigest",DigestText(journalRaw)),("planDigest",DigestText(planRaw)),("custodyDigest",DigestText(custodyRaw)),("inventoryDigest",DigestText(inventoryRaw))})Add(pair.Item1,pair.Item2);
   string current=ScenarioAuthority("epochAuthorization",true);Check(current==""||current==AuthorityValue(q,"authorization"),"current epoch authority contradicts accepted bytes");
   Observe(new {operation="epoch-disposition-facts",newEffectState,currentStateDigest=output["currentStateDigest"].GetString()});
   return JsonSerializer.Serialize(output);
  }catch(Exception error){Observe(new {operation="epoch-disposition-inspection-rejected",reason=error.Message});return "{unavailable}";}
 }
