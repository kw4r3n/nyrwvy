import assert from 'node:assert/strict'
import {readFile,writeFile,mkdir,readdir,rm} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {createHash} from 'node:crypto'
import {arg} from '../offline-leader-jsonrpc/cases.mjs'
import {maxFrameBytes} from './author.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex'),rows=[],proof=[]
const command=(id,op,label,extra={})=>JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:{op,label,...extra}}})
const prefix=[command(1,'begin','member',{target:1,mode:'spawn'}),command(2,'wait','settled',{target:1})]
const integration=command('same-integration-id','integrate','held',{target:1})
const distinct=command('new-integration-id','integrate','held',{target:1})
const phases=r=>Object.fromEntries(['commit','head','save'].map(p=>[p,r.events.filter(x=>x.operation==='effect-persisted'&&x.phase===p).length]))
const total=list=>Object.fromEntries(['commit','head','save'].map(p=>[p,list.reduce((n,r)=>n+phases(r)[p],0)]))
const keyFile=(root,key)=>join(root,hash(key)+'.json'),registryFile=hash('registry/v1')+'.json'
const before=async root=>{await mkdir(root,{recursive:true});return Object.fromEntries(await Promise.all((await readdir(root)).sort().map(async f=>[f,await readFile(join(root,f),'utf8')])))}
const recordKey='journal/v1/'+hash(JSON.stringify('same-integration-id'))
const token='journal:'+hash(JSON.stringify('same-integration-id'))+':'+hash(integration)
async function execute(name,index,{restorationDisabled=false,crash='',evidence='',snapshotCommit='',line=integration,fault='',witness=false,withPrefix=true,inspect=false,startupFailure=false,precommands=[]}={}){
 const root=join(dir,'runs',name),storageDir=join(root,'state');await mkdir(root,{recursive:true})
 const commitGateFile=join(root,'commit-release'),journalGateFile=join(root,'journal-release'),fixture=join(root,`scenario-${index}.json`)
 await rm(commitGateFile,{force:true});await rm(journalGateFile,{force:true})
 const scenario={kind:'transport',patch:'diff',providers:{1:'complete',2:witness?'hold':'complete'},autoMember:'true',storageDir,commitGateFile,journalGateFile,crashWindow:crash,evidence,snapshotCommit}
 await writeFile(fixture,JSON.stringify(scenario));const storageBefore=await before(storageDir)
 const child=spawn('dotnet',[join(dir,...(restorationDisabled?['restoration-disabled','Runtime.dll']:['Runtime.dll'])),fixture],{stdio:['pipe','pipe','pipe']})
 const events=[],outputs=[],actions=[],waiters=new Set();let failure,exitCode,signal
 const wake=()=>{for(const w of waiters)w()}
 for(const [stream,target] of [[child.stdout,outputs],[child.stderr,events]])createInterface({input:stream}).on('line',line=>{try{target.push(target===events?JSON.parse(line):line)}catch(e){failure=e}wake()})
 child.on('error',e=>{failure=e;wake()});child.stdin.on('error',e=>{if(exitCode===undefined)failure=e;wake()})
 const ended=new Promise(resolve=>child.once('close',(code,sig)=>{exitCode=code;signal=sig;wake();resolve()}))
 const timer=setTimeout(()=>{failure=new Error(`registry probe deadline: ${name}/${index}`);child.kill('SIGKILL');wake()},20000)
 const until=async pred=>{if(pred())return;await new Promise((resolve,reject)=>{const check=()=>{if(pred()){waiters.delete(check);resolve()}else if(failure||exitCode!==undefined){waiters.delete(check);reject(failure??new Error('child exited before observation'))}};waiters.add(check);check()})}
 const write=async bytes=>{actions.push({writeHex:bytes.toString('hex')});await new Promise((resolve,reject)=>child.stdin.write(bytes,e=>e?reject(e):resolve()))}
 const receipt=id=>until(()=>outputs.some(x=>JSON.parse(x).id===id))
 const send=async line=>{await write(Buffer.from(line+'\n'));await receipt(JSON.parse(line).id)}
 try{
  if(startupFailure){await ended;assert.equal(events.find(x=>x.operation==='outcome')?.value,'unresolved');assert.ok(!events.some(x=>x.operation==='read-start'));assert.equal(outputs.length,0)}
  else{
   await until(()=>events.some(x=>x.operation==='read-start'))
   if(inspect)await send(command(10,'inspect','restored',{target:1}))
   if(withPrefix){
    await send(prefix[0]);await until(()=>events.some(x=>x.operation==='gate'&&arg(x,'label')==='released:member'||x.operation==='storage-read'&&x.key==='registry-admission/v1/'+hash('1')&&x.present))
    actions.push({awaitAdmissionDecision:'member'});await send(prefix[1])
   }
   for(const item of precommands)await send(item)
   if(witness){await send(command(7,'begin','witness',{target:2,mode:'spawn'}));await until(()=>events.some(x=>x.operation==='provider'&&arg(x,'id')==='2'))}
   await write(Buffer.from(line+'\n'))
   if(fault){
    await until(()=>events.some(x=>x.operation==='step'&&arg(x,'operation')==='commit'))
    await write(fault==='oversize'?Buffer.alloc(maxFrameBytes+1,65):Buffer.from([0xff,10]))
    await until(()=>events.some(x=>x.operation==='provider-ended'&&x.id===2&&x.cancelled))
    await new Promise(r=>setTimeout(r,350));assert.ok(!events.some(x=>x.operation==='effect-persisted'||x.operation==='close'||x.operation==='outcome'))
    actions.push({heldObservation:{ms:350,witnessCancelled:true,mutation:false,release:false}})
   }
   actions.push({releaseCommit:true});await writeFile(commitGateFile,'release\n')
   if(crash){
    await until(()=>events.some(x=>x.operation==='journal-checkpoint'&&x.phase===crash));actions.push({signal:'SIGKILL',afterCheckpoint:crash});child.kill('SIGKILL');await ended
    assert.equal(signal,'SIGKILL');assert.equal(exitCode,null);assert.ok(!events.some(x=>x.operation==='outcome'))
   }else{
    if(!fault){await receipt(JSON.parse(line).id);actions.push({eof:true});child.stdin.end()}
    await ended
   }
  }
  if(failure)throw failure
  if(!crash){
   const outcome=events.find(x=>x.operation==='outcome');assert.ok(outcome);assert.equal(exitCode,0);assert.equal(signal,null)
   assert.equal(outcome.Opened,1);assert.equal(outcome.Closed,1);assert.equal(outcome.Active,0)
   assert.equal(events.filter(x=>x.operation==='input-fd-closed').length,1);assert.equal(events.filter(x=>x.operation==='transport-close').length,1)
   assert.equal(events.filter(x=>x.operation==='read-start').length,events.filter(x=>x.operation==='read-ended').length)
   if(fault){assert.equal(outcome.ok,false);assert.equal(outcome.type,fault==='oversize'?'InvalidDataException':'DecoderFallbackException');assert.equal(outcome.readerFaultIdentity,true);assert.equal(outcome.readerFaultStack,true)}
   else if(!startupFailure){assert.equal(outcome.ok,true);assert.equal(outcome.isOk,true);assert.equal(outcome.TerminalWritten,true)}
  }
  assert.ok(events.filter(x=>x.operation==='protected-step'||x.operation==='storage-written'&&(x.key.startsWith('outcome')||x.key==='registry/v1'&&JSON.parse(x.value).phase==='ready')).every(x=>x.cancelled===false),'determined publication uses clear shield token')
  const row={name,index,restorationDisabled,crash,fault,scenario,actions,events,outputs,exitCode,signal,storageBefore,storageAfter:await before(storageDir)};rows.push(row);return row
 }finally{clearTimeout(timer);if(exitCode===undefined){child.kill('SIGKILL');await ended}}
}
const reply=r=>r.outputs.map(JSON.parse).filter(x=>['same-integration-id','new-integration-id'].includes(x.id)).at(-1)?.result?.outcome
const registry=r=>r.storageAfter[registryFile]&&JSON.parse(r.storageAfter[registryFile])
const terminal=r=>r.outputs.map(JSON.parse).find(x=>x.method==='session/terminal')?.params.report
const inspected=r=>r.events.filter(x=>x.operation==='reply'&&arg(x,'label')==='restored').at(-1)&&arg(r.events.filter(x=>x.operation==='reply'&&arg(x,'label')==='restored').at(-1),'record')
const hasHead=report=>assert.match(report,/1,completed,w1,base,0,false,commit1,passed,integrated-head;/)
const noEffects=r=>assert.deepEqual(phases(r),{commit:0,head:0,save:0})
const noProvider=r=>assert.ok(!r.events.some(x=>['provider','snapshot','workspaceCreate'].includes(x.operation)))
for(const restorationDisabled of [true,false]){
 const name=restorationDisabled?'restoration-disabled':'restoration',a=await execute(name,1,{restorationDisabled}),b=await execute(name,2,{restorationDisabled,inspect:true,snapshotCommit:'different-commit'}),c=await execute(name,3,{restorationDisabled,line:distinct,inspect:true})
 assert.deepEqual(b.storageBefore,a.storageAfter);assert.deepEqual(c.storageBefore,b.storageAfter);assert.equal(reply(b),'ok:integrated-head');assert.equal(reply(c),'ok:integrated-head')
 const observations={sameIdTerminal:terminal(b),newIdTerminal:terminal(c),sameIdInspect:inspected(b),newIdInspect:inspected(c)}
 if(restorationDisabled){assert.doesNotMatch(terminal(b),/integrated-head/);assert.deepEqual(total([a,b,c]),{commit:2,head:2,save:2});assert.throws(()=>hasHead(terminal(b)),assert.AssertionError)}
 else{hasHead(terminal(a));hasHead(terminal(b));hasHead(terminal(c));hasHead(inspected(b));hasHead(inspected(c));noEffects(b);noEffects(c);noProvider(b);noProvider(c);assert.deepEqual(b.storageAfter,a.storageAfter);assert.equal(registry(c).next,'2');assert.equal(registry(c).entries[0].commit,'commit1');assert.deepEqual(total([a,b,c]),{commit:1,head:1,save:1})}
 proof.push({name,processes:3,total:total([a,b,c]),restorationContractRejected:restorationDisabled,observations})
}
for(const crash of ['before-registry-intent','after-registry-intent','after-intent','after-commit','before-registry-publication','after-registry-publication','before-outcome-receipt','after-effect']){
 const a=await execute(crash,1,{crash}),published=['after-registry-publication','before-outcome-receipt','after-effect'].includes(crash),uncertain=['after-commit','before-registry-publication'].includes(crash)
 const b=await execute(crash,2,{line:distinct,inspect:true,withPrefix:false}),c=await execute(crash,3,{inspect:true})
 if(uncertain){assert.equal(reply(b),'err:unresolved');assert.equal(reply(c),'err:unresolved');assert.deepEqual(b.storageAfter,a.storageAfter);assert.deepEqual(c.storageAfter,a.storageAfter);noEffects(b);noEffects(c);assert.deepEqual(total([a,b,c]),crash==='after-commit'?{commit:1,head:0,save:0}:{commit:1,head:1,save:1})}
 else if(published){assert.equal(reply(b),'ok:integrated-head');assert.equal(reply(c),'ok:integrated-head');hasHead(inspected(b));hasHead(terminal(b));hasHead(terminal(c));noEffects(b);noEffects(c);assert.deepEqual(total([a,b,c]),{commit:1,head:1,save:1})}
 else if(crash==='before-registry-intent'){assert.equal(reply(b),'ok:integrated-head');assert.equal(reply(c),'ok:integrated-head');assert.deepEqual(total([a,b,c]),{commit:1,head:1,save:1})}
 else{assert.equal(reply(b),'err:unresolved');assert.deepEqual(b.storageAfter,a.storageAfter);noEffects(b);assert.equal(reply(c),'ok:integrated-head');assert.deepEqual(total([a,b,c]),{commit:1,head:1,save:1})}
 noProvider(b);noProvider(c)
 proof.push({name:crash,processes:3,sigkill:true,published,uncertain,newId:reply(b),sameId:reply(c),total:total([a,b,c]),retainedRegistryPhase:registry(a)?.phase})
}
for(const crash of ['before-registry-publication','after-registry-publication','before-outcome-receipt','after-effect']){
 const name='cancel-'+crash,a=await execute(name,1,{crash,fault:'utf8',witness:true}),b=await execute(name,2,{inspect:true,withPrefix:false}),c=await execute(name,3,{line:distinct,inspect:true,withPrefix:false})
 const actual=a.events.find(x=>x.operation==='storage-write-attempt'&&x.key==='registry/v1'&&JSON.parse(x.value).phase==='ready');assert.equal(JSON.parse(actual.value).outcome,'err:cancelled')
 if(crash==='before-registry-publication'){assert.equal(reply(b),'err:unresolved');assert.equal(reply(c),'err:unresolved');assert.deepEqual(b.storageAfter,a.storageAfter);assert.deepEqual(c.storageAfter,a.storageAfter)}
 else{assert.equal(reply(b),'err:cancelled');assert.equal(reply(c),'ok:integrated-head');hasHead(inspected(b));hasHead(terminal(b));hasHead(terminal(c));assert.equal(registry(a).outcome,'err:cancelled')}
 noProvider(b);noProvider(c);assert.deepEqual(total([a,b,c]),{commit:1,head:1,save:1})
 proof.push({name,processes:3,sigkill:true,actualDeterminedOutcome:'err:cancelled',sameId:reply(b),newId:reply(c),total:total([a,b,c]),restoredWitness:registry(b)?.entries.find(x=>x.id==='2')?.status})
}
{
 const name='unavailable',a=await execute(name,1,{crash:'after-intent'}),b=await execute(name,2,{evidence:'unavailable',withPrefix:false}),c=await execute(name,3,{line:distinct,withPrefix:false,evidence:'unavailable'}),d=await execute(name,4,{withPrefix:false})
 assert.equal(reply(b),'err:unresolved');assert.equal(reply(c),'err:unresolved');assert.deepEqual(b.storageAfter,a.storageAfter);assert.deepEqual(c.storageAfter,a.storageAfter);assert.equal(reply(d),'ok:integrated-head');assert.deepEqual(total([a,b,c,d]),{commit:1,head:1,save:1})
 proof.push({name,processes:4,noAutomaticUnknownRetry:true,total:total([a,b,c,d])})
}
{
 const name='payload-conflict',a=await execute(name,1,{crash:'after-intent'}),b=await execute(name,2,{line:command('same-integration-id','integrate','different',{target:1}),withPrefix:false})
 assert.equal(reply(b),'err:payload-conflict');assert.deepEqual(b.storageAfter,a.storageAfter);noEffects(b);assert.ok(!b.events.some(x=>x.operation==='effect-evidence'))
 proof.push({name,processes:2,conflictBeforeEffectObservation:true})
}
{
 const name='changed-selected-intent',a=await execute(name,1,{crash:'after-intent'}),changed=registry(a)
 changed.entries[0].commit='different-commit';await writeFile(keyFile(join(dir,'runs',name,'state'),'registry/v1'),JSON.stringify(changed))
 const b=await execute(name,2,{withPrefix:false});assert.equal(reply(b),'err:unresolved');noEffects(b);noProvider(b);assert.deepEqual(b.storageAfter,b.storageBefore)
 proof.push({name,processes:2,absentEffectsWithChangedSelectedIntentDoNotRetry:true})
}
{
 const name='member-admission-conflict',a=await execute(name,1),b=await execute(name,2,{withPrefix:false,line:command(1,'begin','changed-member-payload',{target:1,mode:'spawn'})})
 assert.equal(JSON.parse(b.outputs.find(x=>JSON.parse(x).id===1)).result.outcome,'err:payload-conflict');noEffects(b);noProvider(b);assert.deepEqual(b.storageAfter,a.storageAfter)
 proof.push({name,processes:2,admissionConflictBeforeProvider:true})
}
// Damage controls derive their inputs from this fresh completed domain.
const seed=await execute('damage-seed',1)
for(const [name,change] of [
 ['malformed',()=>'{broken'],['oversized',()=> 'x'.repeat(4097)],
 ['version',v=>({...v,version:'2'})],['duplicate-member',v=>({...v,entries:[...v.entries,...v.entries]})],
 ['invalid-next',v=>({...v,next:'1'})],['invalid-eligibility',v=>({...v,entries:v.entries.map(e=>({...e,settled:'false'}))})],
 ['inferred-outcome',v=>({...v,outcomeSource:''})]]){
 const root=join(dir,'runs','damage-'+name,'state');await mkdir(root,{recursive:true})
 for(const [file,value] of Object.entries(seed.storageAfter))await writeFile(join(root,file),value)
 const changed=change(registry(seed));await writeFile(join(root,registryFile),typeof changed==='string'?changed:JSON.stringify(changed))
 const r=await execute('damage-'+name,1,{startupFailure:true});noEffects(r);noProvider(r);assert.deepEqual(r.storageAfter,r.storageBefore)
 proof.push({name:r.name,processes:1,closedBeforeInput:true,storageUnchanged:true})
}
{
 const name='receipt-mismatch',root=join(dir,'runs',name,'state');await mkdir(root,{recursive:true})
 for(const [file,value] of Object.entries(seed.storageAfter))await writeFile(join(root,file),value)
 await writeFile(keyFile(root,'outcome-receipt/'+token),'err:different')
 const r=await execute(name,1,{startupFailure:true});noEffects(r);noProvider(r);assert.deepEqual(r.storageBefore,r.storageAfter)
  proof.push({name,processes:1,closedBeforeInput:true,conflictingActualEvidenceDoesNotRewrite:true})
}
{
 const name='missing-registry',root=join(dir,'runs',name,'state');await mkdir(root,{recursive:true})
 for(const [file,value] of Object.entries(seed.storageAfter))if(file!==registryFile)await writeFile(join(root,file),value)
 const r=await execute(name,1,{startupFailure:true});noEffects(r);noProvider(r);assert.deepEqual(r.storageBefore,r.storageAfter)
 proof.push({name,processes:1,nonemptyDomainWithoutRegistryClosesAdmission:true})
}
for(const fault of ['utf8','oversize']){
 const name='reader-'+fault,a=await execute(name,1,{fault,witness:true}),b=await execute(name,2,{withPrefix:false,inspect:true}),c=await execute(name,3,{line:distinct,withPrefix:false})
 assert.equal(reply(b),'err:cancelled');assert.equal(reply(c),'ok:integrated-head');hasHead(terminal(b));hasHead(terminal(c));assert.deepEqual(total([a,b,c]),{commit:1,head:1,save:1});noProvider(b);noProvider(c)
 proof.push({name,processes:3,exactReaderException:true,protectedPublication:true,total:total([a,b,c])})
}
for(const [name,precommands,expected] of [
 ['discarded',[command(3,'discard','discard',{target:1})],'err:discarded']]){
 const a=await execute(name,1,{precommands}),b=await execute(name,2,{line:distinct,withPrefix:false,inspect:true})
 assert.equal(reply(a),expected);assert.equal(reply(b),expected);noEffects(a);noEffects(b);noProvider(b)
 assert.equal(registry(b).entries[0][name],'true');assert.equal(registry(b).entries[0].commit,'commit1')
 proof.push({name,processes:2,restoredEligibilityRejects:true,noMutation:true})
}
{
 const name='superseded',root=join(dir,'runs',name,'state');await mkdir(root,{recursive:true})
 for(const [file,value] of Object.entries(seed.storageAfter))await writeFile(join(root,file),value)
 const changed=registry(seed);changed.entries[0].superseded='true';await writeFile(join(root,registryFile),JSON.stringify(changed))
 const a=await execute(name,1,{line:distinct,withPrefix:false}),b=await execute(name,2,{line:distinct,withPrefix:false})
 assert.equal(reply(a),'err:superseded');assert.equal(reply(b),'err:superseded');noEffects(a);noEffects(b);noProvider(a);noProvider(b);assert.equal(registry(b).entries[0].superseded,'true')
 proof.push({name,processes:2,constructedEligibilityInputFromFreshSeed:true,restoredEligibilityRejects:true,noMutation:true})
}
{
 const name='unrepresentable-checkpoint',r=await execute(name,1,{snapshotCommit:'c'.repeat(4097)})
 assert.equal(reply(r),'err:unresolved');noEffects(r);assert.ok(!r.events.some(x=>x.operation==='effect-identity'||x.operation==='storage-written'&&x.key.startsWith('journal/')))
 proof.push({name,processes:1,oversizedCheckpointStopsBeforeIntentOrMutation:true})
}
await writeFile(join(dir,'registry-raw.json'),JSON.stringify(rows,null,2)+'\n')
await writeFile(join(dir,'registry-proof.json'),JSON.stringify({scope:'offline Linux pipes; SIGKILL; flushed single-writer domain; synthetic effects; owned task reconstruction',rows:rows.length,proof,hashes:{raw:hash(await readFile(join(dir,'registry-raw.json'))),source:hash(await readFile(new URL('./test-runtime.mjs',import.meta.url)))},identity:{recordKey,token,registryKey:'registry/v1'}},null,2)+'\n')
console.log(`PASS: ${rows.length} registry processes; restored HEAD/Member/admission, paired control, interrupted publication, unknown effects and reader contracts`)
