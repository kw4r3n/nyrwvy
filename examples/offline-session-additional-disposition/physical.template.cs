 // Mechanical terminal evidence; graph owns acceptance and publication.
 const string AdditionalDispositionIntent="additional-disposition/v1/intent",AdditionalDispositionReceipt="additional-disposition/v1/receipt";
 public NyStr AdditionalDispositionFacts(){try{return InspectAdditionalDisposition();}catch(Exception error){Observe(new {operation="terminal-inspection-rejected",reason=error.Message});return "{unavailable}";}}
 string InspectAdditionalDisposition(){
  EpochOwnerPath();
  string acceptedRaw=System.IO.File.ReadAllText(StoragePath("additional/v1/intent"));using var acceptedDoc=JsonDocument.Parse(acceptedRaw);var q=acceptedDoc.RootElement;
  string token=AdditionalToken(q),identity=Required(q,"integrationIdentity");
  var custody=new List<object>();
  // Require every terminal slot before any physical inspection. A late witness
  // cannot repair an active launch; existing verifier checks exact joins.
  for(int stage=0;stage<3;stage++){
   string key="physical-epoch-child/v1/"+DigestText(token)+"/"+stage,launch=System.IO.File.ReadAllText(StoragePath(key));var child=JsonSerializer.Deserialize<GitChild>(launch)!;
   Check(child.State=="quiescent"&&child.Pid.HasValue,"complete terminal custody required");
   string witness=System.IO.File.ReadAllText(StoragePath("physical-join/v1/"+child.Invocation));custody.Add(new {key,stage,launch,witness,launchDigest=DigestText(launch),witnessDigest=DigestText(witness)});
  }
  AdditionalChildGuard();
  string inherited=InspectAdditional();using var inheritedDoc=JsonDocument.Parse(inherited);var prior=inheritedDoc.RootElement;
  string planRaw=System.IO.File.ReadAllText(StoragePath(PlanKey(token)));var plan=LoadPlan(token);var physical=PhysicalState();
  string effectState=Corroborated(plan,physical);Check(!plan.NoMutation&&effectState=="applied","fully owned applied additional plan required");
  Check(plan.Selected==Required(q,"selectedCommit")&&plan.Before.Head==Required(q,"baselineHead")&&DigestText(JsonSerializer.Serialize(plan.Before))==Required(q,"baselineStateDigest"),"exact additional selection and baseline required");
  foreach(string phase in new[]{"commit","head","save"}){
   string file=StoragePath("physical-receipt/"+token+"/"+phase);
   Check(phase=="save"||System.IO.File.Exists(file),"completed commit/head receipts required");
   if(System.IO.File.Exists(file))Check(System.IO.File.ReadAllText(file)==JsonSerializer.Serialize(new {phase,identity=token,plan.Commit}),"exact owned physical receipt required");
  }
  string registryRaw=System.IO.File.ReadAllText(StoragePath("additional-registry/v1")),journalRaw=System.IO.File.ReadAllText(StoragePath("additional-journal/v1/"+identity));using var regDoc=JsonDocument.Parse(registryRaw);var reg=regDoc.RootElement;
  Check(Required(reg,"intent")==plan.Intent,"additional registry plan correlation");using var intentDoc=JsonDocument.Parse(plan.Intent);string member=intentDoc.RootElement.GetProperty("target").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
  // Physical receipts remain optional at the worktree checkpoint, but any
  // existing commit/head/save record is correlated by Corroborated above.
  var inventory=new SortedDictionary<string,string>(StringComparer.Ordinal);
  foreach(string path in System.IO.Directory.GetFiles(StorageRoot)){if(path==StoragePath(AdditionalDispositionIntent)||path==StoragePath(AdditionalDispositionReceipt))continue;inventory.Add(System.IO.Path.GetFileName(path),DigestText(System.IO.File.ReadAllText(path)));}
  string inventoryRaw=JsonSerializer.Serialize(inventory),custodyRaw=JsonSerializer.Serialize(custody,new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
  var output=new Dictionary<string,object>(StringComparer.Ordinal);void Add(string k,string v)=>output[k]=v;
  foreach(var pair in new[]{("additionalRequest",acceptedRaw),("activation",System.IO.File.ReadAllText(StoragePath("additional/v1/activation"))),("predecessorDigest",Required(prior,"predecessorDigest")),("blockedDigest",DigestText(System.IO.File.ReadAllText(StoragePath("additional/v1/blocked")))),("originalRegistry",Required(prior,"sourceRegistry")),("registry",registryRaw),("journal",journalRaw),("plan",planRaw),("custody",custodyRaw),("inventory",inventoryRaw),("integrationRequest",Required(q,"integrationRequest")),("integrationIdentity",identity),("integrationTypedId",JsonDocument.Parse(Required(q,"integrationRequest")).RootElement.GetProperty("id").GetRawText()),("effectIdentity",token),("repository",GitRoot),("repositoryId",Setting("fixtureId")),("member",member),("next",Required(reg,"next")),("fingerprint",Required(reg,"fingerprint")),("intent",plan.Intent),("newEffectState",effectState),("currentStateDigest",DigestText(JsonSerializer.Serialize(physical))),("allNewSlots","quiescent"),("registryDigest",DigestText(registryRaw)),("journalDigest",DigestText(journalRaw)),("planDigest",DigestText(planRaw)),("custodyDigest",DigestText(custodyRaw)),("inventoryDigest",DigestText(inventoryRaw))})Add(pair.Item1,pair.Item2);
  string authority=AuthorityValue(q,"authorization"),supplied=ScenarioAuthority("additionalAuthorization",true);Check(supplied==""||supplied==authority,"additional authority label contradicts accepted bytes");
  var reserved=prior.GetProperty("reservedIds").EnumerateArray().Select(x=>x.GetString()!).ToList();
  foreach(string k in new[]{"id","epoch","memberIdentity","integrationIdentity"})reserved.Add(Required(q,k));reserved.Add(token);
  foreach(string frame in prior.GetProperty("wireFrames").EnumerateArray().Select(x=>x.GetString()!).Concat(new[]{Required(q,"memberRequest"),Required(q,"integrationRequest")})){
   using var d=JsonDocument.Parse(frame);var id=d.RootElement.GetProperty("id");reserved.Add(id.ValueKind==JsonValueKind.String?id.GetString()!:id.GetRawText());reserved.Add(id.GetRawText());reserved.Add(DigestText(id.GetRawText()));
  }
  foreach(string k in new[]{"registry/v1","epoch-registry/v1","additional-registry/v1"}){using var d=JsonDocument.Parse(System.IO.File.ReadAllText(StoragePath(k)));reserved.Add(Required(d.RootElement,"token"));foreach(var entry in d.RootElement.GetProperty("entries").EnumerateArray())reserved.Add(Required(entry,"admission"));}
  output["reservedIds"]=reserved.Distinct(StringComparer.Ordinal).ToArray();output["authorities"]=prior.GetProperty("authorities").EnumerateArray().Select(x=>x.GetString()!).Append(authority).ToArray();
  string raw=JsonSerializer.Serialize(output);Observe(new {operation="terminal-facts",newEffectState=effectState,characters=raw.Length,currentStateDigest=output["currentStateDigest"]});return raw;
 }
