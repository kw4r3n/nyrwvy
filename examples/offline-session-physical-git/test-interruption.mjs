// Controlled offline single-writer crash/invalid-record regressions only.
import assert from 'node:assert/strict'
import {readFile,writeFile,cp,rm,chmod} from 'node:fs/promises'
import {join} from 'node:path'
import {suite,slot,store,untouched,uncertain,until,exists,alive} from './continuation-fixture.mjs'
import {seed,interruptionRequest,interruptionKey,interruptedAuthority} from './interruption-fixture.mjs'
import {hash,git} from './git-fixture.mjs'
const s=await suite(process.argv[2],process.argv[3]),contracts=[]
const dispose=(d,q,options={})=>s.run(d,{admin:'interruptedContinuation',request:q,settings:interruptedAuthority,...options})
function preserved(d,r){for(const [k,v] of Object.entries(d.original))assert.equal(r.after[k],v,k);uncertain(r.after);assert.equal(r.after[slot('physical-continuation-result/v1/continue-id')],undefined)}
async function reject(d,q,options={}){const r=await dispose(d,q,options);assert.deepEqual(r.outputs,['err:unresolved']);untouched(r);return r}
async function closed(d,options={}){const r=await s.run(d,options);untouched(r);assert.ok(!r.events.some(e=>e.operation==='read-start'));uncertain(r.after);return r}
async function accepted(d,q,options={}){
 const r=await dispose(d,q,options);assert.deepEqual(r.outputs,['ok:retained-unresolved-continuation:'+q.expectedState]);preserved(d,r)
 assert.deepEqual(r.physicalBefore,r.physicalAfter);assert.ok(!r.events.some(e=>e.operation==='read-start'||e.operation==='physical-git-owner-start'||e.operation==='storage-write-attempt'))
 assert.deepEqual(Object.keys(r.after).filter(k=>r.before[k]!==r.after[k]),[slot(interruptionKey)])
 for(const [k,v] of Object.entries(r.before))assert.equal(r.after[k],v,k)
 const proof=JSON.parse(r.after[slot(interruptionKey)]);assert.equal(proof.requestDigest,hash(typeof q==='string'?q:JSON.stringify(q)));assert.equal(proof.originalOutcome,'not-inferred');assert.equal(proof.continuationOutcome,'not-inferred');assert.equal(proof.admission,'closed');return r
}
async function noAdmissionOrRetry(d){
 await closed(d)
 for(const id of ['continue-id','distinct-continuation']){const r=await s.run(d,{admin:'continuation',request:{...d.q,id},settings:{dispositionAuthorization:'offline-owner-retain'}});assert.deepEqual(r.outputs,['err:unresolved']);untouched(r);preserved(d,r)}
 const r=await s.run(d,{admin:'release',request:{id:'release-id',authorization:'offline-owner-release',action:'release-observation-admission',continuationId:'continue-id',continuationDigest:'missing',observedStateDigest:'missing',registryDigest:'missing'}});assert.deepEqual(r.outputs,['err:unresolved']);untouched(r);preserved(d,r)
}
const windows={
 'object-only':['after-continuation-intent','before-continuation-mutation-0','after-continuation-mutation-0','before-continuation-mutation-1','after-continuation-mutation-1','before-continuation-result'],
 'ref-only':['after-continuation-intent','before-continuation-mutation-0','after-continuation-mutation-0','before-continuation-result']
}
for(const [state,values] of Object.entries(windows))for(const window of values){
 const d=await seed(s,state+'-'+window,state,window),q=await interruptionRequest(d);await closed(d)
 await accepted(d,q);const replay=await dispose(d,q);untouched(replay);assert.deepEqual(replay.outputs,['ok:retained-unresolved-continuation:'+q.expectedState]);preserved(d,replay)
 await reject(d,JSON.stringify(q)+'\n');await reject(d,{...q,id:'different-disposition'});await noAdmissionOrRetry(d)
 contracts.push({name:d.root.split('/').at(-1),state:q.expectedState,accepted:true,exactReplay:true,distinctIdRefused:true,bothOutcomesMissing:true,startupClosed:true})
}
// A slot published before process creation has no terminal custody. Absence of
// that PID cannot turn it into the separate absent-before-creation case.
for(const state of ['object-only','ref-only']){
 const d=await seed(s,'prelaunch-'+state,state,'before-git-child-start'),q=await interruptionRequest(d)
 await reject(d,q);await closed(d);const c=JSON.parse((await store(join(d.root,'state')))[slot('physical-continuation-child/v1/continue-id/0')]);assert.equal(c.State,'active');assert.equal(c.Pid,null)
 contracts.push({name:'prelaunch-'+state,accepted:false,noPidDoesNotJoin:true})
}
// All required strings keep the separate opaque-authority / ASCII-ID schemas.
{
 const d=await seed(s,'values'),q=await interruptionRequest(d)
 const bad=[undefined,null,0,false,{},[],'']
 for(const [i,value] of bad.entries())for(const side of ['both','request','current']){
  const request={...q,authorization:side==='current'?q.authorization:value},settings={...interruptedAuthority,interruptedContinuationAuthorization:side==='request'?q.authorization:value}
  await reject(d,request,{settings});contracts.push({name:'required-'+i+'-'+side,accepted:false})
 }
 for(const id of [undefined,null,0,false,{},[],'','bad\n','bad\r\n','bad id','日本語','x'.repeat(81),'continue-id','retain-id',JSON.parse((await store(join(d.root,'state')))[slot('physical-continuation-intent/v1')]).Identity])await reject(d,{...q,id})
 for(const field of ['action','continuationId','continuationRequest','continuationIntentDigest','custodyDigest','evidenceDigest','expectedState','observedStateDigest','storageContract'])await reject(d,{...q,[field]:'changed'})
 for(const setting of [{evidence:'unavailable'},{physicalControl:'receipt-absence'},{worktreeModeControl:'content-only'},{interruptedContinuationStorageContract:''}])await reject(d,q,{settings:{...interruptedAuthority,...setting}})
 for(const authority of ['offline-owner-retain','offline-owner-continue'])await reject(d,{...q,authorization:authority},{settings:{...interruptedAuthority,interruptedContinuationAuthorization:authority}})
 for(const key of ['dispositionAuthorization','continuationAuthorization'])for(const value of [null,0,false,{},[],'relabeled',' Offline-owner-retain '])await reject(d,q,{settings:{...interruptedAuthority,[key]:value}})
 for(const [name,authority] of [' ','\t\r\n',' opaque ','null','権限'].entries()){
  const storageName='positive-'+name;await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});const request={...q,authorization:authority},settings={...interruptedAuthority,interruptedContinuationAuthorization:authority,dispositionAuthorization:'',continuationAuthorization:''}
  await accepted(d,request,{storageName,settings});const replay=await dispose(d,request,{storageName,settings});untouched(replay);assert.ok(replay.outputs[0].startsWith('ok:'))
  await reject(d,request,{storageName,settings:{...settings,interruptedContinuationAuthorization:authority+' '}});contracts.push({name:'opaque-'+name,accepted:true,authority,exactOpaqueSemantics:true})
 }
 for(const [name,setting] of [['omitted',{}],['exact',{dispositionAuthorization:'offline-owner-retain',continuationAuthorization:'offline-owner-continue'}]]){
  const storageName='optional-'+name;await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});await accepted(d,q,{storageName,settings:{...interruptedAuthority,...setting}})
 }
 await accepted(d,q);for(const key of ['dispositionAuthorization','continuationAuthorization'])await reject(d,q,{settings:{...interruptedAuthority,[key]:null}})
 await reject(d,{...q,authorization:'offline-owner-continue'},{settings:{...interruptedAuthority,interruptedContinuationAuthorization:'offline-owner-continue',continuationAuthorization:'relabeled'}})
 const path=join(d.root,'state',slot(interruptionKey)),valid=await readFile(path);const proof=JSON.parse(valid);await writeFile(path,JSON.stringify({...proof,result:'ok:invented'}));await reject(d,q);await closed(d)
 contracts.push({name:'values',malformedAndRelabeledAuthoritiesRefused:true,contradictoryDispositionRefused:true})
}
// Rebind the decision to each damaged copy's new hashes, so refusal exercises
// custody/lineage checks, rather than merely a stale observed digest.
{
 const d=await seed(s,'invalid-custody','object-only','after-continuation-mutation-0'),map=await store(join(d.root,'state')),ck='physical-continuation-child/v1/continue-id/0',child=JSON.parse(map[slot(ck)]),wk='physical-join/v1/'+child.Invocation
 const damage=[
  ['child-version',ck,x=>({...x,Version:'3'})],['child-owner',ck,x=>({...x,OwnerDigest:'a'.repeat(64)})],['child-args',ck,x=>({...x,Args:['read-tree','--reset','-u','changed']})],['child-input',ck,x=>({...x,InputDigest:hash('changed')})],['child-no-pid',ck,x=>({...x,Pid:null})],['child-flags',ck,x=>({...x,InputClosed:false})],['child-exit',ck,x=>({...x,ExitCode:7})],['child-repository',ck,x=>({...x,Repository:d.root})],
  ['witness-version',wk,x=>({...x,Version:'2'})],['witness-input-open',wk,x=>({...x,InputClosed:false})],['witness-output-open',wk,x=>({...x,OutputDrained:false})],['witness-owner',wk,x=>({...x,OwnerDigest:'a'.repeat(64)})],['witness-pid',wk,x=>({...x,OwnerPid:0})],['witness-child',wk,x=>({...x,ChildPid:0})],['witness-args',wk,x=>({...x,Args:['update-ref','changed']})],['witness-input',wk,x=>({...x,InputDigest:hash('changed')})],['witness-launch',wk,x=>({...x,LaunchDigest:'a'.repeat(64)})],['witness-capture',wk,x=>({...x,OutputDigest:'a'.repeat(64)})],['witness-error-capture',wk,x=>({...x,ErrorDigest:'b'.repeat(64)})],['witness-digest-lf',wk,x=>({...x,OutputDigest:x.OutputDigest+'\n'})],['witness-exit',wk,x=>({...x,ExitCode:null,ExitSignal:''})],['witness-signal',wk,x=>({...x,ExitCode:0,ExitSignal:'SIGKILL'})]
 ]
 for(const [name,key,change] of damage){const storageName='copy-'+name;await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});await writeFile(join(d.root,storageName,slot(key)),JSON.stringify(change(JSON.parse(map[slot(key)]))));const q=await interruptionRequest(d,{storageName});await reject(d,q,{storageName});contracts.push({name,accepted:false,freshObservedDigest:true})}
 for(const key of [wk,'physical-disposition/v1/retain-id','physical-plan/v1/'+child.Identity]){
  const storageName='missing-'+hash(key).slice(0,8);await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});const q=await interruptionRequest(d,{storageName});await rm(join(d.root,storageName,slot(key)));await reject(d,q,{storageName})
 }
 // A missing earlier stage followed by a retained later slot is contradictory.
 const storageName='out-of-order';await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});await writeFile(join(d.root,storageName,slot('physical-continuation-child/v1/continue-id/1')),map[slot(ck)]);await rm(join(d.root,storageName,slot(ck)));await reject(d,await interruptionRequest(d,{storageName}),{storageName})
 for(const boundary of ['continuation','retention'])for(const value of [null,0,false,{},[],'']){
  const storageName='accepted-'+boundary+'-'+hash(JSON.stringify(value)).slice(0,8);await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});const intent=JSON.parse(map[slot('physical-continuation-intent/v1')]),c=JSON.parse(intent.Request)
  if(boundary==='continuation')c.authorization=value;else{const q=JSON.parse(c.retentionRequest);q.authorization=value;c.retentionRequest=JSON.stringify(q)}intent.Request=JSON.stringify(c);intent.RequestDigest=hash(intent.Request)
  await writeFile(join(d.root,storageName,slot('physical-continuation-intent/v1')),JSON.stringify(intent));await reject(d,await interruptionRequest(d,{storageName}),{storageName});contracts.push({name:'accepted-'+boundary+'-'+JSON.stringify(value),accepted:false,malformedAcceptedAuthority:true})
 }
 for(const [name,key,data] of [['new-result','physical-continuation-result/v1/continue-id','{"result":"ok:invented"}'],['original-outcome','outcome/'+child.Identity,'err:cancelled'],['receipt','physical-receipt/'+child.Identity+'/save',JSON.stringify({phase:'save',identity:child.Identity,Commit:'changed'})],['release','physical-observation-release/v1','{}'],['extra','unowned','{}']]){
  const storageName='copy-'+name;await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});await writeFile(join(d.root,storageName,slot(key)),data);await reject(d,await interruptionRequest(d,{storageName}),{storageName})
 }
}
for(const damage of ['mode','worktree','index','object','ref']){
 const d=await seed(s,'unowned-'+damage),q=await interruptionRequest(d)
 if(damage==='mode')await chmod(join(d.f.root,'file.txt'),0o755)
 if(damage==='worktree')await writeFile(join(d.f.root,'file.txt'),'unowned\n')
 if(damage==='index')git(d.f.root,['read-tree',d.f.selected])
 if(damage==='object')git(d.f.root,['hash-object','-w','--stdin'],'unowned\n')
 if(damage==='ref')git(d.f.root,['update-ref','refs/heads/unowned',d.f.selected])
 await reject(d,q);await closed(d);contracts.push({name:'unowned-'+damage,accepted:false})
}
// A real active new child must acquire its own exact terminal witness. Root/PID
// observations only support test cleanup. The old offered state cannot be reused.
for(const [state,held,mode] of [['object-only','update-ref','late'],['object-only','read-tree','late'],['ref-only','read-tree','nonzero'],['object-only','update-ref','deadline']]){
 const d=await seed(s,'active-'+held+'-'+mode,state,null);await s.wrapper(d)
 if(mode==='nonzero')await writeFile(join(d.root,'nonzero'),'exit7')
 const killed=await s.run(d,{admin:'continuation',request:d.q,settings:{dispositionAuthorization:'offline-owner-retain',gitDeadlineMs:mode==='deadline'?'1500':'10000'},held}),intent=JSON.parse(killed.after[slot('physical-continuation-intent/v1')]),stage=held==='read-tree'&&state==='object-only'?1:0,child=JSON.parse(killed.after[slot('physical-continuation-child/v1/continue-id/'+stage)]),helper=JSON.parse(await readFile(join(d.root,'held.json'))),early=await interruptionRequest(d)
 try{
  if(mode!=='deadline'){assert.ok(await alive(helper.pid));await reject(d,early);await closed(d);await writeFile(join(d.root,'child-release'),'release')}
  await until(()=>exists(join(d.root,'state',slot('physical-join/v1/'+child.Invocation))),'new terminal witness');await until(async()=>!await alive(child.Pid)&&!await alive(helper.pid),'new helper termination')
  const terminal=JSON.parse((await store(join(d.root,'state')))[slot('physical-join/v1/'+child.Invocation)])
  if(mode==='deadline'){assert.equal(terminal.ExitSignal,'SIGKILL');assert.equal(terminal.ExitCode,null)}
  if(mode==='nonzero')assert.equal(terminal.ExitCode,7)
  await reject(d,early);const q=await interruptionRequest(d);await accepted(d,q);const replay=await dispose(d,q);untouched(replay);assert.ok(replay.outputs[0].startsWith('ok:'));await noAdmissionOrRetry(d)
  contracts.push({name:d.root.split('/').at(-1),accepted:true,activeFencePreserved:true,witnessExit:terminal.ExitCode,witnessSignal:terminal.ExitSignal,lateStateOnly:true,newResultMissing:true})
 }finally{await writeFile(join(d.root,'child-release'),'release');await until(async()=>!await alive(child.Pid)&&!await alive(helper.pid),'active cleanup')}
}
for(const window of ['before-interrupted-disposition-publication','after-interrupted-disposition-publication']){
 const d=await seed(s,window,'ref-only'),q=await interruptionRequest(d),killed=await dispose(d,q,{crash:window});preserved(d,killed);assert.deepEqual(killed.outputs,[]);assert.deepEqual(killed.physicalBefore,killed.physicalAfter)
 if(window.startsWith('before-'))await accepted(d,q);else{const replay=await dispose(d,q);untouched(replay);assert.deepEqual(replay.outputs,['ok:retained-unresolved-continuation:ref-only'])}
 await reject(d,{...q,id:'distinct-id'});await reject(d,{...q,expectedState:'applied'});await noAdmissionOrRetry(d);contracts.push({name:window,explicitReplayOnly:true,admissionClosed:true})
}
// Actual new success is a different terminal contract and remains exact.
{
 const d=await seed(s,'actual-completion','ref-only','after-continuation-result'),map=await store(join(d.root,'state')),q=await interruptionRequest(d);await reject(d,q);assert.equal((await store(join(d.root,'state')))[slot('physical-continuation-result/v1/continue-id')],map[slot('physical-continuation-result/v1/continue-id')]);contracts.push({name:'actual-completion',accepted:false,actualOutcomePreserved:true})
}
await writeFile(join(s.out,'raw.json'),JSON.stringify(s.rows,null,2)+'\n')
const helpers=new Set();for(const r of s.rows)for(const e of r.events)if(e.operation==='physical-git-owner-start')helpers.add(e.pid)
for(const pid of helpers)assert.equal(await alive(pid),false,'helper still running '+pid)
const proof={processes:s.rows.length,domains:s.domains.length,sigkills:s.rows.filter(r=>r.signal==='SIGKILL').length,contracts,rawHash:hash(JSON.stringify(s.rows)),sources:Object.fromEntries(await Promise.all(['test-interruption.mjs','interruption-fixture.mjs','interruption.template.cs','continuation.template.cs','continuation-fixture.mjs','compile.mjs','git.template.cs','git-owner.mjs','disposition.template.cs'].map(async file=>[file,hash(await readFile(new URL(file,import.meta.url)))]))),helperObservations:[...helpers].map(pid=>({pid,running:false})),scope:'one trusted serialized writer, intact flushed storage and immutable fixture provisioning; controlled silent Linux SHA1 single-file continuation stages; both missing outcomes and all original/new fences retained; no admission release or public graph recovery'}
await writeFile(join(s.out,'proof.json'),JSON.stringify(proof,null,2)+'\n');console.log(`PASS: ${proof.processes} applications/${proof.domains} fresh domains/${proof.sigkills} kills; interrupted disposition preserves both unknown outcomes, all records and closed admission`)
