import assert from 'node:assert/strict'
import {readFile} from 'node:fs/promises'
import {createHash} from 'node:crypto'
import {compileRegistry} from '../offline-session-registry/compile.mjs'
const root=new URL('../offline-session-physical-git/',import.meta.url),load=async p=>readFile(new URL(p,root),'utf8'),hash=x=>createHash('sha256').update(x).digest('hex')
const replace=(s,a,b)=>{assert.equal(s.split(a).length,2,a);return s.replace(a,b)}
let physical=await load('git.template.cs'),disposition=await load('disposition.template.cs'),continuation=await load('continuation.template.cs'),interruption=(await load('interruption.template.cs')).replace('@@CONTINUATION_OWNER_DIGEST@@',hash(await load('git-owner.mjs')))
physical=replace(physical,'GitGuard();GitChildGuard();','GitGuard();if(epochMode)EpochChildGuard();else GitChildGuard();')
physical=replace(physical,'if(mutation&&Setting("gitOwner")!="")return OwnedGit(args,input,trim);','if(mutation&&epochMode){EpochOwnerPath();return OwnedGit(args,input,trim,EpochLaunchKey(args));}\n  if(mutation&&Setting("gitOwner")!="")return OwnedGit(args,input,trim);')
physical=replace(physical,'  if(mutation){','  if(mutation){\n   Check(!epochMode,"epoch direct Git mutation is prohibited");')
physical=replace(physical,'AtomicWrite(PlanKey(identity),JsonSerializer.Serialize(plan));','Checkpoint("before-physical-intent");AtomicWrite(PlanKey(identity),JsonSerializer.Serialize(plan));')
disposition=replace(disposition,'Check(launchKey=="physical-child/v1"||',String.raw`Check(epochMode&&System.Text.RegularExpressions.Regex.IsMatch(launchKey,@"\Aphysical-epoch-child/v1/[0-9a-f]{64}/[012]\z")&&!System.IO.File.Exists(StoragePath(launchKey))||launchKey=="physical-child/v1"||`)
disposition=replace(disposition,'string owner=System.IO.Path.GetFullPath(Setting("gitOwner"));','string owner=epochMode?EpochOwnerPath():System.IO.Path.GetFullPath(Setting("gitOwner"));')
disposition=replace(disposition,'Check(child.Identity!="","owned mutation requires identity");','if(epochMode)Check(child.OwnerDigest==SupportedEpochOwner,"epoch launch owner differs from pin");\n  Check(child.Identity!="","owned mutation requires identity");')
const successor=await readFile(new URL('../offline-session-epoch-disposition/physical.template.cs',import.meta.url),'utf8'),additionalRaw=await readFile(new URL('../offline-session-additional-admission/physical.template.cs',import.meta.url),'utf8')
const additional=replace(additionalRaw,'string next=Required(r,"next");','foreach(string k in new[]{AdditionalDispositionIntent,AdditionalDispositionReceipt})allowed.Add(System.IO.Path.GetFileName(StoragePath(k)));\n  string next=Required(r,"next");');
const terminal=await readFile(new URL('./physical.template.cs',import.meta.url),'utf8');
const ownerBytes=await readFile(new URL('../offline-session-epoch/git-owner.mjs',import.meta.url));let epoch=(await readFile(new URL('../offline-session-epoch/physical.template.cs',import.meta.url),'utf8')).replace('@@EPOCH_OWNER_DIGEST@@',hash(ownerBytes)).replace('@@EPOCH_OWNER_LENGTH@@',String(ownerBytes.length))
// Extend only the successor inventory; the published epoch binding stays exact.
epoch=replace(epoch,'string[] admin={', 'allowed.Add(System.IO.Path.GetFileName(StoragePath(EpochDispositionIntent)));allowed.Add(System.IO.Path.GetFileName(StoragePath(EpochDispositionReceipt)));\n  string[] admin={')
epoch=replace(epoch,'if(name=="epochRequest")','if(name=="epochRequest"||name=="epochDispositionRequest"||name=="additionalRequest"||name=="additionalDispositionRequest")')
epoch=replace(epoch,'void EpochChildGuard(){','void EpochChildGuard()=>EpochChildGuardFor("epoch/v1/intent","physical-epoch-child/v1/");\n void EpochChildGuardFor(string intentKey,string childPrefix){')
epoch=replace(epoch,'StoragePath("epoch/v1/intent");if(!System.IO.File.Exists(file))return;','StoragePath(intentKey);if(!System.IO.File.Exists(file))return;')
epoch=replace(epoch,'string key="physical-epoch-child/v1/"+DigestText(token)+"/"+stage;\n   if(!System.IO.File.Exists(StoragePath(key))){gap=true;continue;}','string key=childPrefix+DigestText(token)+"/"+stage;\n   if(!System.IO.File.Exists(StoragePath(key))){gap=true;continue;}')

await compileRegistry(process.argv[2],{hostMethods:{fixtureRead:'FixtureRead',physicalFacts:'PhysicalFacts',epochDispositionFacts:'EpochDispositionFacts',additionalFacts:'AdditionalFacts',additionalDispositionFacts:'AdditionalDispositionFacts'},effectsTransform:s=>{
 s=replace(s,' @@METHODS@@',physical.replace('if(epochMode)EpochChildGuard();else GitChildGuard();','if(epochMode){EpochChildGuard();if(additionalMode)AdditionalChildGuard();}else GitChildGuard();')+'\n'+disposition+'\n'+continuation+'\n'+interruption+'\n'+epoch+'\n'+successor+'\n'+additional+'\n'+terminal+'\n @@METHODS@@')
 s=replace(s,'this.scenario=scenario;','this.scenario=scenario;epochMode=true;additionalMode=true;Checkpoint("before-input");')
 // The new graph receives exact opaque storage. The inherited read firewall
 // remains the predicate for old procedures and predecessor reconstruction.
 s=replace(s,'public NyOption<NyStr> StorageRead(NyStr key){','public NyOption<NyStr> StorageRead(NyStr key){if(epochMode)EpochOwnerPath();')
 s=replace(s,'NyOption<NyStr>.Some(System.IO.File.ReadAllText(file))','NyOption<NyStr>.Some(epochMode?System.IO.File.ReadAllText(file):PhysicalRead(key.ToString(),System.IO.File.ReadAllText(file)))')
 s=replace(s,'public NyUnit StorageWrite(NyStr key,NyStr value){','public NyUnit StorageWrite(NyStr key,NyStr value){\n if(epochMode)EpochOwnerPath();Check(!observationAdmission,"observation admission cannot write application storage");Check(key.ToString()!="registry/v1"&&!key.ToString().StartsWith("journal/v1/",StringComparison.Ordinal),"original application evidence immutable");')
 s=replace(s,'  AtomicWrite(k,v);Observe','  if(k=="epoch-registry/v1"){Checkpoint(v.Contains("\\\"phase\\\":\\\"pending\\\"",StringComparison.Ordinal)?"before-epoch-registry-intent":"before-epoch-registry-publication");PhysicalPrepare(v);}\n if(k.StartsWith("epoch-journal/v1/",StringComparison.Ordinal))PhysicalPrepareJournal(v);\n if(k.StartsWith("epoch-disposition/v1/",StringComparison.Ordinal))Checkpoint("before-disposition-"+k.Substring(21));\n if(k.StartsWith("epoch/v1/",StringComparison.Ordinal))Checkpoint("before-"+k.Substring(9));\n AtomicWrite(k,v);if(k.StartsWith("epoch-disposition/v1/",StringComparison.Ordinal))Checkpoint("after-disposition-"+k.Substring(21));if(k.StartsWith("epoch/v1/",StringComparison.Ordinal))Checkpoint("after-"+k.Substring(9));if(k.StartsWith("epoch-journal/v1/",StringComparison.Ordinal)&&v.Contains("\\\"state\\\":\\\"pending\\\"",StringComparison.Ordinal))Checkpoint("after-epoch-journal-intent");if(k=="epoch-registry/v1")Checkpoint(v.Contains("\\\"phase\\\":\\\"pending\\\"",StringComparison.Ordinal)?"after-epoch-registry-intent":"after-epoch-registry-publication");Observe')
 s=replace(s,'PersistEffect(phase,patch.ToString());','PhysicalStep(phase,patch.ToString());')
 s=replace(s,'public NyResult<NyUnit,NyStr> WorkspaceCreate(NyStr workspace,NyStr basis){Log(', 'public NyResult<NyUnit,NyStr> WorkspaceCreate(NyStr workspace,NyStr basis){Checkpoint("before-fresh-workspace");Log(')
 s=replace(s,'  return NyUnit.Value;\n }\n public NyUnit EffectIdentity',String.raw`  if(k.StartsWith("epoch-journal/v1/",StringComparison.Ordinal)&&v.Contains("\"state\":\"done\"",StringComparison.Ordinal))Checkpoint("after-epoch-journal-completion");
  return NyUnit.Value;
 }
 public NyUnit EffectIdentity`)
 s=replace(s,'"head"=>new[]{"integrated-head"}','"head"=>new[]{PhysicalHead()}')
 s=replace(s,'public NyStr EffectEvidence(NyStr identity){','public NyStr EffectEvidence(NyStr identity)=>PhysicalEvidence(identity.ToString());\n NyStr SyntheticEvidence(NyStr identity){')
 s=s.replaceAll('k=="epoch-registry/v1"','k=="additional-registry/v1"').replaceAll('k.StartsWith("epoch-journal/v1/",','k.StartsWith("additional-journal/v1/",').replaceAll('before-epoch-','before-additional-').replaceAll('after-epoch-','after-additional-');
 s=replace(s,'AtomicWrite(k,v);if(k.StartsWith(', 'if(k.StartsWith("additional/v1/",StringComparison.Ordinal)||k.StartsWith("additional-lineage/v1/",StringComparison.Ordinal))Checkpoint("before-additional-write-"+k);AtomicWrite(k,v);if(k.StartsWith("additional/v1/",StringComparison.Ordinal)||k.StartsWith("additional-lineage/v1/",StringComparison.Ordinal))Checkpoint("after-additional-write-"+k);if(k.StartsWith(');
 s=replace(s,'AtomicWrite(k,v);if(k.StartsWith(', 'if(k.StartsWith("additional-disposition/v1/",StringComparison.Ordinal))Checkpoint("before-terminal-"+k.Substring(26));AtomicWrite(k,v);if(k.StartsWith("additional-disposition/v1/",StringComparison.Ordinal))Checkpoint("after-terminal-"+k.Substring(26));if(k.StartsWith(');
 return s
}})
