import assert from 'node:assert/strict'
import {readFile} from 'node:fs/promises'
import {createHash} from 'node:crypto'
import {compileRegistry} from '../offline-session-registry/compile.mjs'
const physical=await readFile(new URL('./git.template.cs',import.meta.url),'utf8')
const disposition=await readFile(new URL('./disposition.template.cs',import.meta.url),'utf8')
const continuation=await readFile(new URL('./continuation.template.cs',import.meta.url),'utf8')
const interruption=(await readFile(new URL('./interruption.template.cs',import.meta.url),'utf8')).replace('@@CONTINUATION_OWNER_DIGEST@@',createHash('sha256').update(await readFile(new URL('./git-owner.mjs',import.meta.url))).digest('hex'))
const replace=(source,from,to)=>{assert.equal(source.split(from).length,2,from);return source.replace(from,to)}
await compileRegistry(process.argv[2],{effectsTransform:source=>{
 source=replace(source,' @@METHODS@@',physical+'\n'+disposition+'\n'+continuation+'\n'+interruption+'\n @@METHODS@@')
 source=replace(source,'this.scenario=scenario;', 'this.scenario=scenario;if(Setting("interruptedContinuationRequest")!="")RunInterruptedDisposition();if(Setting("continuationRequest")!=""||Setting("releaseRequest")!="")RunContinuation();if(Setting("dispositionRequest")!="")RunDisposition();')
 source=replace(source,'NyOption<NyStr>.Some(System.IO.File.ReadAllText(file))','NyOption<NyStr>.Some(PhysicalRead(key.ToString(),System.IO.File.ReadAllText(file)))')
 source=replace(source,'public NyUnit StorageWrite(NyStr key,NyStr value){','public NyUnit StorageWrite(NyStr key,NyStr value){\n  Check(!observationAdmission,"released observation admission cannot write application storage");')
 assert.equal(source.split('if(k=="registry/v1")Checkpoint(').length,3)
 source=source.replace('if(k=="registry/v1")Checkpoint(', 'if(k=="registry/v1")PhysicalPrepare(v);\n  if(k=="registry/v1")Checkpoint(')
 source=replace(source,'  AtomicWrite(k,v);Observe', '  if(k.StartsWith("journal/v1/",StringComparison.Ordinal))PhysicalPrepareJournal(v);\n  AtomicWrite(k,v);Observe')
 source=replace(source,'PersistEffect(phase,patch.ToString());','PhysicalStep(phase,patch.ToString());')
 source=replace(source,'"head"=>new[]{"integrated-head"}', '"head"=>new[]{PhysicalHead()}')
 source=replace(source,'public NyStr EffectEvidence(NyStr identity){','public NyStr EffectEvidence(NyStr identity)=>PhysicalEvidence(identity.ToString());\n NyStr SyntheticEvidence(NyStr identity){')
 return source
}})
