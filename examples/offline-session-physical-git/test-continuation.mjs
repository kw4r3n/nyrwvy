// A new physical request/result, then separately bound observation admission.
// Original missing success/cancellation stays unknown in every fresh domain.
import assert from 'node:assert/strict'
import {readFile,writeFile,cp,chmod,rm} from 'node:fs/promises'
import {join} from 'node:path'
import {suite,slot,store,until,exists,alive,line,untouched,uncertain} from './continuation-fixture.mjs'
import {hash,git} from './git-fixture.mjs'
const s=await suite(process.argv[2],process.argv[3]),contracts=[]
const authority={dispositionAuthorization:'offline-owner-retain',continuationAuthorization:'offline-owner-continue'}
async function seed(name,state='object-only',{active=false}={}){
 const d=await s.domain(name)
 if(active){
  await s.wrapper(d);if(state==='absent')await writeFile(join(d.root,'nonzero'),'exit7');const a=await s.run(d,{prefix:true,held:state==='ref-only'?'update-ref':'commit-tree'}),c=JSON.parse(a.after[slot('physical-child/v1')]),helper=JSON.parse(await readFile(join(d.root,'held.json')))
  try{await writeFile(join(d.root,'child-release'),'release');await until(()=>exists(join(d.root,'state',slot('physical-join/v1/'+c.Invocation))),'original join');await until(async()=>!await alive(c.Pid)&&!await alive(helper.pid),'original helpers')}
  finally{await writeFile(join(d.root,'child-release'),'release');await until(async()=>!await alive(c.Pid)&&!await alive(helper.pid),'cleanup')}
 }else await s.run(d,{prefix:true,crash:state==='object-only'?'after-git-object':state==='applied'?'after-git-worktree':'after-git-ref'})
 const retention=await s.retain(d,state),original=await store(join(d.root,'state')),prior=original[slot('physical-disposition/v1/retain-id')]
 const q={id:'continue-id',authorization:'offline-owner-continue',action:'continue-owned-tail',retentionRequest:JSON.stringify(retention),dispositionDigest:hash(prior),expectedState:state,observedStateDigest:JSON.parse(prior).stateDigest}
 return {d,q,original}
}
function preserved(original,row){for(const [k,v] of Object.entries(original))assert.equal(row.after[k],v,k);uncertain(row.after)}
const continuation=(d,q,options={})=>s.run(d,{admin:'continuation',request:q,settings:authority,...options})
async function reject(d,q,options={}){const r=await continuation(d,q,options);assert.deepEqual(r.outputs,['err:unresolved']);untouched(r);return r}
async function releaseRequest(d,id='release-id'){
 const map=await store(join(d.root,'state')),raw=map[slot('physical-continuation-result/v1/continue-id')],r=JSON.parse(raw)
 return {id,authorization:'offline-owner-release',action:'release-observation-admission',continuationId:'continue-id',continuationDigest:hash(raw),observedStateDigest:r.stateDigest,registryDigest:r.registryDigest}
}
const release=(d,q,options={})=>s.run(d,{admin:'release',request:q,settings:authority,...options})
async function closed(d){const r=await s.run(d);untouched(r);assert.ok(!r.events.some(e=>e.operation==='read-start'));uncertain(r.after);return r}
for(const state of ['object-only','ref-only']){
 const {d,q,original}=await seed('active-success-'+state,state,{active:true});await closed(d)
 const a=await continuation(d,q);assert.match(a.outputs[0],/^ok:continued-owned-tail:[0-9a-f]{40}$/);preserved(original,a)
 const starts=a.events.filter(e=>e.operation==='physical-git-owner-start');assert.deepEqual(starts.map(x=>x.command),state==='object-only'?['update-ref','read-tree']:['read-tree']);assert.ok(!starts.some(x=>x.command==='commit-tree'))
 assert.equal(a.physicalAfter.worktree,'selected\n');assert.equal(a.physicalAfter.status,'');assert.equal(JSON.parse(a.after[slot('physical-child/v1')]).State,'active')
 const replay=await continuation(d,q);untouched(replay);assert.deepEqual(replay.outputs,a.outputs);preserved(original,replay);await reject(d,{...q,id:'different-id'});await reject(d,{...q,authorization:'changed'});await closed(d)
 const rq=await releaseRequest(d);for(const field of ['authorization','action','continuationId','continuationDigest','observedStateDigest','registryDigest']){const bad=await release(d,{...rq,[field]:'changed'});assert.deepEqual(bad.outputs,['err:unresolved']);untouched(bad)}
 const released=await release(d,rq);assert.deepEqual(released.outputs,['ok:released-observation-admission']);assert.deepEqual(released.physicalBefore,released.physicalAfter);preserved(original,released)
 const again=await release(d,rq);untouched(again);assert.deepEqual(again.outputs,released.outputs)
 const conflict=await release(d,{...rq,id:'different-release'});assert.deepEqual(conflict.outputs,['err:unresolved']);untouched(conflict)
 const admitted=await s.run(d,{frames:[line('inspect-id','inspect',{target:1}),line('original-id','integrate',{target:1}),line('distinct-id','integrate',{target:1}),line('new-member','begin',{target:2,mode:'spawn'}),line('discard-id','discard',{target:1})]});untouched(admitted);preserved(original,admitted)
 assert.ok(admitted.events.some(e=>e.operation==='read-start'));assert.ok(admitted.events.some(e=>e.operation==='physical-observation-admission'));assert.ok(!admitted.events.some(e=>['provider','snapshot','workspaceCreate'].includes(e.operation)))
 const responses=admitted.outputs.map(JSON.parse);for(const id of ['original-id','distinct-id','new-member','discard-id'])assert.equal(responses.find(x=>x.id===id).result.outcome,'err:unresolved')
 assert.ok(responses.find(x=>x.id==='inspect-id'));assert.equal(JSON.parse(admitted.after[slot('registry/v1')]).entries[0].integrated,'')
 contracts.push({name:state,activeOriginalPreserved:true,commands:starts.map(x=>x.command),distinctContinuationRefused:true,readAdmission:true,originalAndDistinctMutationsBlocked:true,originalOutcomeMissing:true})
 // Corrupt new decision/result/custody copies, without changing original seeds.
 for(const [label,key,damage] of [
  ['result','physical-continuation-result/v1/continue-id',x=>({...x,result:'ok:changed'})],
  ['pending','physical-continuation-intent/v1',x=>({...x,PlanDigest:'changed'})],
  ['release','physical-observation-release/v1',x=>({...x,resultDigest:'changed'})],
  ['admission','physical-observation-intent/v1',x=>({...x,registryDigest:'changed'})],
  ['release-result','physical-observation-result/v1/release-id',x=>({...x,result:'ok:changed'})],
  ['new-custody','physical-continuation-child/v1/continue-id/0',x=>({...x,InputClosed:false})],
  ['new-arguments','physical-continuation-child/v1/continue-id/0',x=>({...x,Args:['commit-tree']})],
  ['request-lineage','physical-observation-intent/v1',x=>({...x,requestDigest:'changed'})]]){
  const storageName='copy-'+label;await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});const file=join(d.root,storageName,slot(key));await writeFile(file,JSON.stringify(damage(JSON.parse(await readFile(file)))))
  const r=await s.run(d,{storageName});untouched(r);assert.ok(!r.events.some(e=>e.operation==='read-start'));preserved(original,r)
 }
 const storageName='copy-added-outcome';await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});const child=JSON.parse(original[slot('physical-child/v1')]);await writeFile(join(d.root,storageName,slot('outcome/'+child.Identity)),'ok:invented');const extra=await s.run(d,{storageName});untouched(extra);assert.ok(!extra.events.some(e=>e.operation==='read-start'))
 for(const key of ['physical-continuation-result/v1/continue-id','physical-observation-result/v1/release-id','physical-observation-release/v1']){
  const storageName='copy-missing-'+key.split('/')[0];await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});await rm(join(d.root,storageName,slot(key)));const r=await s.run(d,{storageName});untouched(r);assert.ok(!r.events.some(e=>e.operation==='read-start'));preserved(original,r)
 }
}
// Identical-ID conflicting payloads and authoritative unchanged/unsupported tails.
{
 const {d,q}=await seed('decision-negatives')
 for(const field of ['authorization','action','dispositionDigest','expectedState','observedStateDigest'])await reject(d,{...q,[field]:'changed'})
 for(const id of ['',q.id+'\n',q.id+'\r\n','a'.repeat(81),null,0])await reject(d,{...q,id})
 for(const field of ['invocation','identity','repository','repositoryId','launchDigest','planDigest','expectedState','authorization']){const prior=JSON.parse(q.retentionRequest);prior[field]='changed';await reject(d,{...q,retentionRequest:JSON.stringify(prior)})}
 await reject(d,q,{settings:{...authority,evidence:'unavailable'}});await reject(d,q,{settings:{...authority,physicalControl:'receipt-absence'}});await reject(d,q,{settings:{...authority,worktreeModeControl:'content-only'}});await reject(d,q,{settings:{...authority,gitOwner:''}})
 await reject(d,{...q,authorization:'offline-owner-retain'},{settings:{...authority,continuationAuthorization:'offline-owner-retain'}})
 contracts.push({name:'decision-negatives',separateAuthorityRequired:true,correlationAndObservedStateRequired:true,unavailableRefused:true})
}
for(const state of ['absent','applied']){const {d,q}=await seed('unsupported-'+state,state,{active:state==='absent'});await reject(d,q);contracts.push({name:'unsupported-'+state,observedButNotAuthorizedForContinuation:true})}
for(const damage of ['mode','worktree','index','object','ref','partial-restoration','missing-disposition','receipt','missing-witness','false-drain','old-active','no-pid','original-journal','original-outcome','orphan-result']){
 const {d,q}=await seed('ownership-'+damage,'object-only',{active:['missing-witness','false-drain','old-active','no-pid'].includes(damage)})
 if(damage==='mode')await chmod(join(d.f.root,'file.txt'),0o755)
 if(damage==='worktree')await writeFile(join(d.f.root,'file.txt'),'unowned\n')
 if(damage==='index')git(d.f.root,['read-tree',d.f.selected])
 if(damage==='object')git(d.f.root,['hash-object','-w','--stdin'],'unowned\n')
 if(damage==='ref')git(d.f.root,['update-ref','refs/heads/unowned',d.f.selected])
 if(damage==='partial-restoration')git(d.f.root,['read-tree','--reset','-u',d.f.selected])
 const map=await store(join(d.root,'state')),child=JSON.parse(map[slot('physical-child/v1')]),wfile=join(d.root,'state',slot('physical-join/v1/'+child.Invocation))
 if(damage==='missing-disposition')await rm(join(d.root,'state',slot('physical-disposition/v1/retain-id')))
 if(damage==='receipt')await writeFile(join(d.root,'state',slot('physical-receipt/'+child.Identity+'/save')),JSON.stringify({phase:'save',identity:child.Identity,Commit:JSON.parse(map[slot('physical-plan/v1/'+child.Identity)]).Commit}))
 if(damage==='missing-witness')await rm(wfile)
 if(damage==='false-drain'){const witness=JSON.parse(await readFile(wfile));witness.OutputDrained=false;await writeFile(wfile,JSON.stringify(witness))}
 if(damage==='old-active'||damage==='no-pid'){child[damage==='old-active'?'Version':'Pid']=damage==='old-active'?'1':null;await writeFile(join(d.root,'state',slot('physical-child/v1')),JSON.stringify(child))}
 if(damage==='original-journal'){const registry=JSON.parse(map[slot('registry/v1')]),j=JSON.parse(map[slot(registry.id)]);j.fingerprint='changed';await writeFile(join(d.root,'state',slot(registry.id)),JSON.stringify(j))}
 if(damage==='original-outcome')await writeFile(join(d.root,'state',slot('outcome/'+child.Identity)),'err:cancelled')
 if(damage==='orphan-result')await writeFile(join(d.root,'state',slot('physical-continuation-result/v1/continue-id')),'{"result":"ok:unbound"}')
 await reject(d,q);contracts.push({name:damage,refusedBeforeNewIntent:true})
}
for(const damage of ['mode','worktree','index','object','ref','unavailable']){
 const {d,q,original}=await seed('released-drift-'+damage,'ref-only');assert.match((await continuation(d,q)).outputs[0],/^ok:continued-owned-tail:/);await release(d,await releaseRequest(d))
 if(damage==='mode')await chmod(join(d.f.root,'file.txt'),0o755)
 if(damage==='worktree')await writeFile(join(d.f.root,'file.txt'),'unowned\n')
 if(damage==='index')git(d.f.root,['read-tree',d.f.base])
 if(damage==='object')git(d.f.root,['hash-object','-w','--stdin'],'unowned\n')
 if(damage==='ref')git(d.f.root,['update-ref','refs/heads/unowned',d.f.selected])
 const r=await s.run(d,{settings:damage==='unavailable'?{evidence:'unavailable'}:{}});untouched(r);preserved(original,r);assert.ok(!r.events.some(e=>e.operation==='read-start'));contracts.push({name:'released-drift-'+damage,releaseRecheckedBeforeInput:true})
}
// Every published new Git intent stays blocked when its actual result is absent.
for(const state of ['object-only','ref-only']){
 const windows=['before-continuation-intent','after-continuation-intent','before-continuation-mutation-0','before-git-child-start','after-continuation-mutation-0',...(state==='object-only'?['before-continuation-mutation-1','after-continuation-mutation-1']:[]),'before-continuation-result','after-continuation-result']
 for(const window of windows){
  const {d,q,original}=await seed(state+'-'+window,state),killed=await continuation(d,q,{crash:window});preserved(original,killed);assert.deepEqual(killed.outputs,[])
  const retried=await continuation(d,q);preserved(original,retried)
  if(window==='before-continuation-intent')assert.match(retried.outputs[0],/^ok:continued-owned-tail:/)
  else if(window==='after-continuation-result'){untouched(retried);assert.match(retried.outputs[0],/^ok:continued-owned-tail:/)}
  else{untouched(retried);assert.deepEqual(retried.outputs,['err:unresolved']);await reject(d,{...q,id:'new-id'});const bad=await release(d,{id:'release-id',authorization:'offline-owner-release',action:'release-observation-admission',continuationId:'continue-id',continuationDigest:'missing',observedStateDigest:'missing',registryDigest:hash(original[slot('registry/v1')])});assert.deepEqual(bad.outputs,['err:unresolved']);untouched(bad);await closed(d)}
  contracts.push({name:state+'-'+window,newResultOnly:window==='after-continuation-result',noAutomaticReplay:window!=='before-continuation-intent',originalOutcomeMissing:true})
 }
}
// A launcher killed with a new owned child still running cannot release or replay.
{
 const {d,q,original}=await seed('new-held-child');await s.wrapper(d);const killed=await continuation(d,q,{held:'update-ref'});preserved(original,killed)
 const child=JSON.parse(killed.after[slot('physical-continuation-child/v1/continue-id/0')]),helper=JSON.parse(await readFile(join(d.root,'held.json')))
 try{assert.ok(await alive(helper.pid));await reject(d,q);await closed(d);await writeFile(join(d.root,'child-release'),'release');await until(()=>exists(join(d.root,'state',slot('physical-join/v1/'+child.Invocation))),'new join');await until(async()=>!await alive(child.Pid)&&!await alive(helper.pid),'new helper termination');await reject(d,q);await closed(d)}
 finally{await writeFile(join(d.root,'child-release'),'release');await until(async()=>!await alive(child.Pid)&&!await alive(helper.pid),'cleanup')}
 contracts.push({name:'new-held-child',custodyUnavailableRefused:true,lateEffectDoesNotSupplyNewResult:true})
}
for(const window of ['before-release-intent','after-release-intent','before-release-result','after-release-result','before-release-activation','after-release-activation']){
 const {d,q,original}=await seed(window,'ref-only');const done=await continuation(d,q);assert.match(done.outputs[0],/^ok:continued-owned-tail:/);const rq=await releaseRequest(d),killed=await release(d,rq,{crash:window});preserved(original,killed);assert.deepEqual(killed.outputs,[])
 if(window!=='after-release-activation')await closed(d)
 const retry=await release(d,rq);preserved(original,retry);assert.deepEqual(retry.outputs,['ok:released-observation-admission']);assert.deepEqual(retry.physicalBefore,retry.physicalAfter)
 const read=await s.run(d,{frames:[line('inspect','inspect',{target:1})]});untouched(read);preserved(original,read);assert.ok(read.events.some(e=>e.operation==='read-start'));contracts.push({name:window,readOnlyReleaseRetry:true,originalOutcomeMissing:true})
}
await writeFile(join(s.out,'raw.json'),JSON.stringify(s.rows,null,2)+'\n')
const helpers=new Set();for(const r of s.rows)for(const e of r.events)if(e.operation==='physical-git-owner-start')helpers.add(e.pid)
for(const pid of helpers)assert.equal(await alive(pid),false,'helper still running '+pid)
const proof={processes:s.rows.length,domains:s.domains.length,sigkills:s.rows.filter(r=>r.signal==='SIGKILL').length,contracts,rawHash:hash(JSON.stringify(s.rows)),sources:Object.fromEntries(await Promise.all(['test-continuation.mjs','continuation-fixture.mjs','continuation.template.cs','git.template.cs','disposition.template.cs','git-owner.mjs','compile.mjs'].map(async file=>[file,hash(await readFile(new URL(file,import.meta.url)))]))),helperObservations:[...helpers].map(pid=>({pid,running:false})),scope:'one serialized trusted writer, intact flushed storage; controlled Linux SHA1 single-file fixtures; observation-only release; public graph-composed recovery absent'}
await writeFile(join(s.out,'proof.json'),JSON.stringify(proof,null,2)+'\n');console.log(`PASS: ${proof.processes} applications/${proof.domains} disposable domains/${proof.sigkills} kills; distinct continuation and observation release; original uncertainty preserved`)
