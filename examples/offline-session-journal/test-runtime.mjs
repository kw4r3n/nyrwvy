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
const prefix=[command(1,'begin','member',{target:1,mode:'spawn'}),command(2,'gate','provider1'),command(3,'gate','snapshot1'),command(4,'gate','release1'),command(5,'wait','settled',{target:1})]
const integration=command('same-integration-id','integrate','held',{target:1})
const phases=r=>Object.fromEntries(['commit','head','save'].map(p=>[p,r.events.filter(x=>x.operation==='effect-persisted'&&x.phase===p).length]))
const keyFile=(root,key)=>join(root,hash(key)+'.json')
const recordKey='journal/v1/'+hash(JSON.stringify('same-integration-id'))
const token='journal:'+hash(JSON.stringify('same-integration-id'))+':'+hash(integration)
const before=async root=>{await mkdir(root,{recursive:true});return Object.fromEntries(await Promise.all((await readdir(root)).sort().map(async f=>[f,await readFile(join(root,f),'utf8')])))}
const total=list=>Object.fromEntries(['commit','head','save'].map(p=>[p,list.reduce((n,r)=>n+phases(r)[p],0)]))
async function execute(name,index,{disabled=false,receiptDisabled=false,lost=false,crash='',evidence='',snapshotCommit='',line=integration,repeat=false,fault='',witness=false,withPrefix=true}={}){
 const root=join(dir,'runs',name),storageDir=join(root,'state');await mkdir(root,{recursive:true})
 const commitGateFile=join(root,'commit-release'),journalGateFile=join(root,'journal-release'),fixture=join(root,`scenario-${index}.json`)
 await rm(commitGateFile,{force:true});await rm(journalGateFile,{force:true})
 const scenario={kind:'transport',patch:'diff',providers:{1:'complete',...(witness?{2:'hold'}:{})},storageDir,commitGateFile,journalGateFile,crashWindow:crash,evidence,snapshotCommit}
 await writeFile(fixture,JSON.stringify(scenario))
 const storageBefore=await before(storageDir)
 const child=spawn('dotnet',[join(dir,...(disabled?['disabled','Runtime.dll']:receiptDisabled?['receipt-disabled','Runtime.dll']:['Runtime.dll'])),fixture],{stdio:['pipe','pipe','pipe']})
 const events=[],outputs=[],actions=[],times=[],waiters=new Set();let failure,exitCode,signal
 const wake=()=>{for(const w of waiters)w()}
 for(const [stream,target] of [[child.stdout,outputs],[child.stderr,events]])createInterface({input:stream}).on('line',line=>{try{target.push(target===events?JSON.parse(line):line);if(target===events)times.push(performance.now())}catch(e){failure=e}wake()})
 child.on('error',e=>{failure=e;wake()});child.stdin.on('error',e=>{failure=e;wake()})
 const ended=new Promise(resolve=>child.once('close',(code,sig)=>{exitCode=code;signal=sig;wake();resolve()}))
 const timer=setTimeout(()=>{failure=new Error('journal probe deadline');child.kill('SIGKILL');wake()},20000)
 const until=async pred=>{if(pred())return;await new Promise((resolve,reject)=>{const check=()=>{if(failure||exitCode!==undefined){waiters.delete(check);reject(failure??new Error('child exited before observation'))}else if(pred()){waiters.delete(check);resolve()}};waiters.add(check);check()})}
 const write=async bytes=>{actions.push({writeHex:bytes.toString('hex')});await new Promise((resolve,reject)=>child.stdin.write(bytes,e=>e?reject(e):resolve()))}
 const receipt=id=>until(()=>outputs.some(x=>JSON.parse(x).id===id))
 const send=async line=>{await write(Buffer.from(line+'\n'));await receipt(JSON.parse(line).id)}
 try{
  await until(()=>events.some(x=>x.operation==='read-start'))
  if(withPrefix)for(const line of prefix)await send(line)
  if(witness){await send(command(7,'begin','witness',{target:2,mode:'spawn'}));await send(command(8,'gate','provider2'))}
  await write(Buffer.from(line+'\n'))
  // Unless the request is a replay/rejection, release the synthetic commit.
  if(lost||fault){
   await until(()=>events.some(x=>x.operation==='step'&&arg(x,'operation')==='commit'))
   if(lost){actions.push({closePeerOutput:true});child.stdout.destroy()}
   if(fault){
    await write(fault==='oversize'?Buffer.alloc(maxFrameBytes+1,65):Buffer.from([0xff,10]))
    await until(()=>events.some(x=>x.operation==='provider-ended'&&x.id===2&&x.cancelled))
    await new Promise(r=>setTimeout(r,350))
    assert.ok(!events.some(x=>x.operation==='effect-persisted'||x.operation==='close'||x.operation==='outcome'))
    actions.push({heldObservation:{ms:350,witnessCancelled:true,mutation:false,release:false}})
   }
  }
  actions.push({releaseCommit:true});await writeFile(commitGateFile,'release\n')
  if(crash){
   await until(()=>events.some(x=>x.operation==='journal-checkpoint'&&x.phase===crash))
   actions.push({signal:'SIGKILL',afterCheckpoint:crash});child.kill('SIGKILL');await ended
   assert.equal(signal,'SIGKILL');assert.equal(exitCode,null);assert.ok(!events.some(x=>x.operation==='outcome'))
  }else{
   if(!lost&&!fault){
    if(JSON.parse(line).id!==undefined)await receipt(JSON.parse(line).id)
    if(repeat){await write(Buffer.from(line+'\n'));await until(()=>outputs.filter(x=>JSON.parse(x).id===JSON.parse(line).id).length===2)}
    actions.push({eof:true});child.stdin.end()
   }
   await ended
   const outcome=events.find(x=>x.operation==='outcome');assert.ok(outcome);assert.equal(exitCode,0);assert.equal(signal,null)
   assert.equal(outcome.Opened,1);assert.equal(outcome.Closed,1);assert.equal(outcome.Active,0)
   assert.equal(events.filter(x=>x.operation==='input-fd-closed').length,1);assert.equal(events.filter(x=>x.operation==='transport-close').length,1)
   assert.equal(events.filter(x=>x.operation==='read-start').length,events.filter(x=>x.operation==='read-ended').length)
   if(lost){assert.equal(outcome.ok,false);assert.equal(outcome.type,'IOException');assert.equal(outcome.OutputFault,'IOException');assert.equal(outcome.TerminalWritten,false);assert.ok(!outputs.some(x=>JSON.parse(x).id==='same-integration-id'));assert.ok(!actions.some(x=>x.eof||x.signal))}
   else if(fault){assert.equal(outcome.ok,false);assert.equal(outcome.type,fault==='oversize'?'InvalidDataException':'DecoderFallbackException');assert.equal(outcome.readerFaultIdentity,true);assert.equal(outcome.readerFaultStack,true)}
   else{assert.equal(outcome.ok,true);assert.equal(outcome.TerminalWritten,true)}
  }
  if(failure)throw failure
  assert.ok(events.filter(x=>x.operation==='protected-step'||['storage-written','storage-write-attempt'].includes(x.operation)&&(x.key.startsWith('outcome/')||x.key.startsWith('outcome-receipt/'))).every(x=>x.cancelled===false),'determined tail and outcome retention protected')
  const row={name,index,disabled,receiptDisabled,lost,crash,fault,scenario,actions,events,outputs,exitCode,signal,storageBefore,storageAfter:await before(storageDir)}
  rows.push(row);return row
 }finally{clearTimeout(timer);if(exitCode===undefined){child.kill('SIGKILL');await ended}}
}
const reply=r=>r.outputs.map(JSON.parse).filter(x=>x.id==='same-integration-id').at(-1)?.result.outcome
const record=r=>JSON.parse(r.storageAfter[hash(recordKey)+'.json'])
const identical=(a,b)=>assert.deepEqual(a.actions.filter(x=>x.writeHex),b.actions.filter(x=>x.writeHex),'same exact bytes/ID across fresh process restart')
for(const disabled of [false,true]){
 const name=disabled?'lost-response-disabled':'lost-response'
 const first=await execute(name,1,{disabled,lost:true}),second=await execute(name,2,{disabled})
 identical(first,second);assert.deepEqual(second.storageBefore,first.storageAfter)
 assert.deepEqual(total([first,second]),disabled?{commit:2,head:2,save:2}:{commit:1,head:1,save:1})
 if(!disabled){assert.equal(record(first).state,'done');assert.equal(reply(second),record(first).outcome);assert.equal(reply(second),'ok:integrated-head');assert.deepEqual(second.storageAfter,first.storageAfter)}
 proof.push({name,processes:2,sameRequestBytes:true,total:total([first,second]),returnedOriginal:!disabled})
}
for(const crash of ['after-intent','after-effect','after-commit']){
 const first=await execute(crash,1,{crash}),second=await execute(crash,2)
 identical(first,second);assert.deepEqual(second.storageBefore,first.storageAfter);assert.equal(record(first).state,'pending')
 const state=crash==='after-intent'?'absent':crash==='after-effect'?'applied':'partial'
 assert.equal(second.events.find(x=>x.operation==='effect-evidence').state,state)
 assert.equal(reply(second),crash==='after-commit'?'err:unresolved':'ok:integrated-head')
 assert.deepEqual(total([first,second]),crash==='after-commit'?{commit:1,head:0,save:0}:{commit:1,head:1,save:1})
 if(crash==='after-commit'){assert.equal(record(second).state,'pending');assert.deepEqual(second.storageAfter,first.storageAfter)}
 proof.push({name:crash,processes:2,evidence:state,total:total([first,second]),result:reply(second)})
}
{
 const first=await execute('unknown-evidence',1,{crash:'after-intent'}),second=await execute('unknown-evidence',2,{evidence:'unavailable'}),third=await execute('unknown-evidence',3)
 assert.equal(reply(second),'err:unresolved');assert.deepEqual(second.storageAfter,first.storageAfter);assert.equal(reply(third),'ok:integrated-head');assert.deepEqual(total([first,second,third]),{commit:1,head:1,save:1})
 proof.push({name:'unknown-evidence',processes:3,unknownDoesNotRetry:true,laterAuthoritativeAbsenceRetries:true,total:total([first,second,third])})
}
for(const pending of [false,true]){
 const name=pending?'conflict-pending':'conflict-done',first=await execute(name,1,pending?{crash:'after-intent'}:{})
 const changed=command('same-integration-id','integrate','changed-payload',{target:1})
 const second=await execute(name,2,{line:changed}),third=await execute(name,3)
 assert.equal(reply(second),'err:payload-conflict');assert.deepEqual(second.storageAfter,first.storageAfter);assert.ok(!second.events.some(x=>x.operation==='effect-evidence'||x.operation==='effect-identity'));assert.equal(reply(third),'ok:integrated-head')
 assert.deepEqual(total([first,second,third]),{commit:1,head:1,save:1})
 proof.push({name,processes:3,conflictBeforeEffectObservation:true,storageUnchanged:true,total:total([first,second,third])})
}
{
 const a=await execute('changed-intent',1,{crash:'after-intent'}),b=await execute('changed-intent',2,{snapshotCommit:'different-commit'}),c=await execute('changed-intent',3)
 identical(a,b);assert.equal(reply(b),'err:unresolved');assert.equal(b.events.find(x=>x.operation==='effect-evidence').state,'absent');assert.deepEqual(a.storageAfter,b.storageAfter);assert.equal(reply(c),'ok:integrated-head');assert.deepEqual(total([a,b,c]),{commit:1,head:1,save:1})
 proof.push({name:a.name,processes:3,samePayloadChangedCandidateDoesNotRetry:true,total:total([a,b,c])})
}
{
 const r=await execute('same-process-replay',1,{repeat:true});assert.equal(reply(r),'ok:integrated-head');assert.deepEqual(phases(r),{commit:1,head:1,save:1});proof.push({name:r.name,processes:1,total:phases(r)})
}
{
 const root=join(dir,'runs','corrupt-journal','state');await mkdir(root,{recursive:true});await writeFile(keyFile(root,recordKey),'{broken')
 const r=await execute('corrupt-journal',1);assert.equal(reply(r),'err:unresolved');assert.deepEqual(r.storageBefore,r.storageAfter);assert.deepEqual(phases(r),{commit:0,head:0,save:0});proof.push({name:r.name,processes:1,storageUnchanged:true,total:phases(r)})
}
{
 const line=command('same-integration-id','integrate','unknown',{target:99}),a=await execute('retained-failure',1,{line}),b=await execute('retained-failure',2,{line})
 assert.equal(reply(a),'err:unknown');assert.equal(reply(b),'err:unknown');assert.deepEqual(a.storageAfter,b.storageAfter);assert.deepEqual(total([a,b]),{commit:0,head:0,save:0});proof.push({name:a.name,processes:2,outcome:'err:unknown',total:total([a,b])})
}
for(const [name,id,op,extra] of [['null-id',null,'integrate',{target:1}],['notification',undefined,'integrate',{target:1}],['async-integrate','same-integration-id','begin',{target:6,previous:1,mode:'integrate'}]]){
 const r=await execute(name,1,{line:command(id,op,'unsupported',extra)});assert.deepEqual(phases(r),{commit:0,head:0,save:0});assert.deepEqual(r.storageBefore,r.storageAfter)
 if(name==='async-integrate')assert.equal(reply(r),'err:stable-synchronous-id-required')
 proof.push({name,processes:1,failedClosed:true,total:phases(r)})
}
for(const fault of ['oversize','utf8']){
 const a=await execute('journal-reader-'+fault,1,{fault,witness:true}),b=await execute('journal-reader-'+fault,2)
 assert.deepEqual(total([a,b]),{commit:1,head:1,save:1});assert.equal(reply(b),record(a).outcome);assert.equal(reply(b),'err:cancelled')
 proof.push({name:a.name,processes:2,exactReaderException:true,protectedOutcome:true,total:total([a,b])})
}
// The reviewed failure combined cancellation with a kill before the separate
// outcome file. Complete effects alone cannot distinguish ok from cancelled.
const receiptFile=hash('outcome-receipt/'+token)+'.json',outcomeFile=hash('outcome/'+token)+'.json'
const acceptsCancelled=r=>assert.equal(reply(r),'err:cancelled','recover the actual determined cancellation')
for(const receiptDisabled of [false,true]){
 const name=receiptDisabled?'cancel-crash-receipt-disabled':'cancel-crash-receipt',a=await execute(name,1,{receiptDisabled,fault:'utf8',witness:true,crash:'after-effect'}),b=await execute(name,2,{receiptDisabled})
 assert.equal(a.events.find(x=>x.operation==='storage-write-attempt'&&x.key==='outcome/'+token).value,'err:cancelled')
 assert.equal(a.storageAfter[outcomeFile],undefined);assert.equal(record(a).state,'pending')
 assert.equal(b.events.find(x=>x.operation==='effect-evidence').state,'applied');assert.deepEqual(total([a,b]),{commit:1,head:1,save:1})
 if(receiptDisabled){
  assert.equal(a.storageAfter[receiptFile],undefined);assert.equal(reply(b),'err:unresolved');assert.equal(record(b).state,'pending');assert.deepEqual(b.storageAfter,a.storageAfter)
  assert.throws(()=>acceptsCancelled(b),assert.AssertionError,'receipt-disabled graph fails the identical actual-outcome recovery contract')
 }else{
  assert.equal(a.storageAfter[receiptFile],'err:cancelled');acceptsCancelled(b);assert.equal(record(b).outcome,'err:cancelled')
 }
 proof.push({name,processes:2,actualDeterminedOutcome:'err:cancelled',receiptPersisted:!receiptDisabled,missingSeparateOutcome:true,completeEffectsDoNotInferSuccess:true,result:reply(b),recoveryContractRejected:receiptDisabled,total:total([a,b])})
}
for(const fault of ['', 'utf8']){
 const name=fault?'cancel-crash-before-receipt':'crash-before-receipt',a=await execute(name,1,{fault,witness:!!fault,crash:'before-outcome-receipt'}),b=await execute(name,2)
 const actual=fault?'err:cancelled':'ok:integrated-head'
 assert.equal(a.events.find(x=>x.operation==='storage-write-attempt'&&x.key==='outcome-receipt/'+token).value,actual)
 assert.equal(a.storageAfter[receiptFile],undefined);assert.equal(a.storageAfter[outcomeFile],undefined)
 assert.equal(b.events.find(x=>x.operation==='effect-evidence').state,'applied');assert.equal(reply(b),'err:unresolved');assert.equal(record(b).state,'pending');assert.deepEqual(b.storageAfter,a.storageAfter);assert.deepEqual(total([a,b]),{commit:1,head:1,save:1})
 proof.push({name,processes:2,actualDeterminedOutcome:actual,noDurableOutcomeReceipt:true,completeEffectsDoNotInferSuccess:true,result:'err:unresolved',total:total([a,b])})
}
// Reuse exact retained 6f4c2c3 storage from author/reviewer observations. Legacy
// completed records may have been inferred; only an actual stored result can
// authenticate them. These are input fixtures, not fresh legacy executions.
const legacy=JSON.parse(await readFile(new URL('./fixtures/legacy-outcome-state.json',import.meta.url),'utf8'))
for(const c of legacy.cases){
 const name=c.name,root=join(dir,'runs',name,'state');await mkdir(root,{recursive:true})
 for(const [file,value] of Object.entries(c.storage))await writeFile(join(root,file),value)
 const r=await execute(name,1);assert.equal(reply(r),c.expected);assert.deepEqual(phases(r),{commit:0,head:0,save:0})
 if(c.expected==='err:unresolved')assert.deepEqual(r.storageAfter,r.storageBefore)
 else{assert.equal(record(r).outcomeSource,'actual');assert.equal(record(r).outcome,c.expected)}
 proof.push({name,processes:1,reusedLegacyState:true,sourceRawHash:c.sourceRawHash,result:reply(r),noMutation:true,authenticated:c.expected!=='err:unresolved'})
}
await writeFile(join(dir,'journal-raw.json'),JSON.stringify(rows,null,2)+'\n')
await writeFile(join(dir,'journal-proof.json'),JSON.stringify({scope:'offline Linux pipes, SIGKILL and flushed retained files; single writer; synthetic commit/HEAD/save; no physical Git/provider effects',rows:rows.length,proof,hashes:{raw:hash(await readFile(join(dir,'journal-raw.json'))),source:hash(await readFile(new URL('./test-runtime.mjs',import.meta.url)))},identity:{recordKey,token,payloadFingerprint:hash(integration)}},null,2)+'\n')
console.log(`PASS: ${rows.length} journal processes; lost-response one mutation vs disabled two; crash, conflict, unresolved, replay and supervised reader contracts`)
