 // Generic fixture storage: opaque keys mapped to SHA256 filenames. Flush the
 // replacement and containing directory before returning. No app journal policy.
 readonly AsyncLocal<string> effectIdentity=new();
 string StorageRoot=>System.IO.Path.GetFullPath(Setting("storageDir"));
 static string DigestText(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
 string StoragePath(string key)=>System.IO.Path.Combine(StorageRoot,DigestText(key)+".json");
 [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int open(string path,int flags);
 [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int fsync(int fd);
 [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int close(int fd);
 void AtomicWrite(string key,string value){
  System.IO.Directory.CreateDirectory(StorageRoot);
  string file=StoragePath(key),temporary=file+".tmp";
  using(var output=new System.IO.FileStream(temporary,System.IO.FileMode.Create,System.IO.FileAccess.Write,System.IO.FileShare.None)){
   var bytes=System.Text.Encoding.UTF8.GetBytes(value);output.Write(bytes);output.Flush(true);
  }
  System.IO.File.Move(temporary,file,true);
  int fd=open(StorageRoot,65536);Check(fd>=0,"open storage directory");
  try{Check(fsync(fd)==0,"fsync storage directory");}finally{Check(close(fd)==0,"close storage directory");}
 }
 public NyStr Digest(NyStr value)=>DigestText(value.ToString());
 public NyOption<NyStr> StorageRead(NyStr key){
  string file=StoragePath(key.ToString());bool present=System.IO.File.Exists(file);
  Observe(new {operation="storage-read",key=key.ToString(),present});
  return present?NyOption<NyStr>.Some(System.IO.File.ReadAllText(file)):NyOption<NyStr>.None();
 }
 void Checkpoint(string phase){
  Observe(new {operation="journal-checkpoint",phase,cancelled=NyRuntime.CancellationRequested});
  if(Setting("crashWindow")!=phase)return;
  var clock=System.Diagnostics.Stopwatch.StartNew();
  while(!System.IO.File.Exists(Setting("journalGateFile"))){Check(clock.ElapsedMilliseconds<15000,"journal checkpoint gate timeout");Thread.Sleep(5);}
 }
 public NyUnit StorageWrite(NyStr key,NyStr value){
  string k=key.ToString(),v=value.ToString();
  Observe(new {operation="storage-write-attempt",key=k,value=v,cancelled=NyRuntime.CancellationRequested});
  if(k.StartsWith("outcome-receipt/",StringComparison.Ordinal))Checkpoint("before-outcome-receipt");
  if(k.StartsWith("outcome/",StringComparison.Ordinal))Checkpoint("after-effect");
  AtomicWrite(k,v);Observe(new {operation="storage-written",key=k,value=v,cancelled=NyRuntime.CancellationRequested});
  if(k.StartsWith("journal/v1/",StringComparison.Ordinal)&&v.Contains("\"state\":\"pending\"",StringComparison.Ordinal))Checkpoint("after-intent");
  return NyUnit.Value;
 }
 public NyUnit EffectIdentity(NyStr identity){effectIdentity.Value=identity.ToString();Observe(new {operation="effect-identity",identity=effectIdentity.Value});return NyUnit.Value;}
 // The fixture's synthetic effect ledger is independent of journal records.
 // Each durable row is the synthetic effect itself; inspection never mutates it.
 void PersistEffect(string phase,string patch){
  string file=StoragePath("synthetic-effects");
  var rows=System.IO.File.Exists(file)?JsonSerializer.Deserialize<List<Dictionary<string,string>>>(System.IO.File.ReadAllText(file))!:new();
  rows.Add(new(){{"identity",effectIdentity.Value??"unidentified"},{"phase",phase},{"patch",patch}});
  AtomicWrite("synthetic-effects",JsonSerializer.Serialize(rows));
  Observe(new {operation="effect-persisted",phase,identity=effectIdentity.Value??"unidentified",patch});
 }
 public NyStr EffectEvidence(NyStr identity){
  string file=StoragePath("synthetic-effects"),state="unknown",head="";
  if(Setting("evidence")!="unavailable"){
   var rows=System.IO.File.Exists(file)?JsonSerializer.Deserialize<List<Dictionary<string,string>>>(System.IO.File.ReadAllText(file))!:new();
   var found=rows.Where(x=>x["identity"]==identity.ToString()).ToArray();
   if(found.Length==0)state="absent";
   else if(found.Length==3&&found[0]["phase"]=="commit"&&found[1]["phase"]=="head"&&found[2]["phase"]=="save"){
    state="applied";head=found[2]["patch"];
   }else state="partial";
  }
  // Effect completion/HEAD cannot determine success versus cancellation.
  // Only a graph-written actual outcome receipt can recover that result.
  var evidence=JsonSerializer.Serialize(new {state,head});
  Observe(new {operation="effect-evidence",identity=identity.ToString(),state,head});return evidence;
 }
