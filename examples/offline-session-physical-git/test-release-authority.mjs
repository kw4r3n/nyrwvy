// Focused release-authority repair: accepted lineage, optional current settings,
// replay/startup and publication windows. Disposable repositories/fake providers.
import assert from 'node:assert/strict'
import {readFile,writeFile,mkdir,cp} from 'node:fs/promises'
import {join,resolve,dirname} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {suite,slot,store,until,exists,alive,line,untouched,uncertain} from './continuation-fixture.mjs'
import {hash,inspect} from './git-fixture.mjs'
const s=await suite(process.argv[2],process.argv[3]),bundle=resolve(process.argv[2]),rows=[],contracts=[]
async function execute(d,{kind='',request,settings={},storage=join(d.root,'state'),crash='',liveAuthority}={}){
 const index=++d.seq,scenario={...d.scenario,...settings,storageDir:storage,crashWindow:crash}
 for(const key of ['continuationRequest','dispositionRequest','releaseRequest','continuationAuthorization','dispositionAuthorization','releaseAuthorization'])delete scenario[key]
 Object.assign(scenario,settings)
 if(kind){scenario[kind+'Request']=join(d.root,`authority-request-${index}.json`);scenario[kind+'Authorization']=liveAuthority??request.authorization;await writeFile(scenario[kind+'Request'],JSON.stringify(request))}
 const path=join(d.root,`authority-scenario-${index}.json`);await writeFile(path,JSON.stringify(scenario))
 const before=await store(storage),physicalBefore=await inspect(d.f.root),events=[],outputs=[]
 const p=spawn('dotnet',[join(bundle,'Runtime.dll'),path],{stdio:['pipe','pipe','pipe']});let code,signal,failure
 const ended=new Promise(r=>p.once('close',(c,z)=>{code=c;signal=z;r()}));p.on('error',e=>failure=e);p.stdin.on('error',()=>{})
 for(const [stream,target] of [[p.stderr,events],[p.stdout,outputs]])createInterface({input:stream}).on('line',text=>{try{target.push(target===events?JSON.parse(text):text)}catch(e){failure=e}})
 p.stdin.end();const timer=setTimeout(()=>p.kill('SIGKILL'),30000)
 try{
  if(crash){await until(()=>{if(failure)throw failure;if(code!==undefined)throw Error('early exit at '+crash);return events.some(e=>e.operation==='journal-checkpoint'&&e.phase===crash)},crash);p.kill('SIGKILL')}
  await ended;if(failure)throw failure;assert.equal(crash?signal:code,crash?'SIGKILL':0)
  const r={name:d.name,index,kind,request,scenario,events,outputs,code,signal,before,after:await store(storage),physicalBefore,physicalAfter:await inspect(d.f.root)};rows.push(r);await writeFile(join(d.root,`authority-row-${index}.json`),JSON.stringify(r,null,2)+'\n');return r
 }finally{clearTimeout(timer);if(code===undefined){p.kill('SIGKILL');await ended}}
}
function preserved(original,row){for(const [key,value] of Object.entries(original))assert.equal(row.after[key],value,key);uncertain(row.after);assert.deepEqual(row.physicalBefore,row.physicalAfter)}
function rejected(row){assert.deepEqual(row.outputs,['err:unresolved']);untouched(row);assert.ok(!row.events.some(e=>e.operation==='read-start'||e.operation==='physical-intent'||e.operation==='storage-write-attempt'))}
async function seed(name,state='ref-only',{active=false,custom=false}={}){
 const d=await s.domain(name);d.name=name
 if(active){
  await s.wrapper(d);const r=await s.run(d,{prefix:true,held:'commit-tree'}),child=JSON.parse(r.after[slot('physical-child/v1')]),helper=JSON.parse(await readFile(join(d.root,'held.json')))
  try{await writeFile(join(d.root,'child-release'),'release');await until(()=>exists(join(d.root,'state',slot('physical-join/v1/'+child.Invocation))),'original join');await until(async()=>!await alive(child.Pid)&&!await alive(helper.pid),'original helper termination')}
  finally{await writeFile(join(d.root,'child-release'),'release');await until(async()=>!await alive(child.Pid)&&!await alive(helper.pid),'cleanup')}
 }else await s.run(d,{prefix:true,crash:'after-git-ref'})
 d.scenario={...s.rows.at(-1).scenario,crashWindow:''}
 const accepted={retention:custom?'accepted-retention-alpha':'offline-owner-retain',continuation:custom?'accepted-continuation-beta':'offline-owner-continue',release:custom?'accepted-observation-gamma':'offline-owner-release'}
 const map=await store(join(d.root,'state')),child=JSON.parse(map[slot('physical-child/v1')])
 const retention={id:'retain-id',authorization:accepted.retention,action:'retain-unresolved',invocation:child.Invocation,identity:child.Identity,repository:d.f.root,repositoryId:d.scenario.fixtureId,launchDigest:hash(map[slot('physical-child/v1')]),planDigest:hash(map[slot('physical-plan/v1/'+child.Identity)]),expectedState:state}
 const retain=await execute(d,{kind:'disposition',request:retention});assert.deepEqual(retain.outputs,['ok:retained-unresolved:'+state])
 const original=retain.after,prior=original[slot('physical-disposition/v1/retain-id')]
 const continuation={id:'continue-id',authorization:accepted.continuation,action:'continue-owned-tail',retentionRequest:JSON.stringify(retention),dispositionDigest:hash(prior),expectedState:state,observedStateDigest:JSON.parse(prior).stateDigest}
 const done=await execute(d,{kind:'continuation',request:continuation,settings:{dispositionAuthorization:accepted.retention}});assert.match(done.outputs[0],/^ok:continued-owned-tail:/)
 for(const [key,value] of Object.entries(original))assert.equal(done.after[key],value);uncertain(done.after)
 const actual=done.after[slot('physical-continuation-result/v1/continue-id')],result=JSON.parse(actual)
 const release={id:'release-id',authorization:accepted.release,action:'release-observation-admission',continuationId:'continue-id',continuationDigest:hash(actual),observedStateDigest:result.stateDigest,registryDigest:result.registryDigest}
 return {d,accepted,original,completed:done.after,release}
}
for(const [state,active,custom] of [['ref-only',false,false],['object-only',true,true]]){
 const {d,accepted:a,original,completed,release}=await seed('authority-'+state,state,{active,custom})
 const bound={continuationAuthorization:a.continuation,dispositionAuthorization:a.retention}
 const cases=[
  ['same-continuation-omitted',a.continuation,{},false],['same-retention-omitted',a.retention,{},false],
  ['same-continuation-relabelled',a.continuation,{continuationAuthorization:'other-continue',dispositionAuthorization:'other-retain'},false],
  ['same-retention-relabelled',a.retention,{continuationAuthorization:'other-continue',dispositionAuthorization:'other-retain'},false],
  ['same-continuation-bound',a.continuation,bound,false],['same-retention-bound',a.retention,bound,false],
  ['distinct-omitted',a.release,{},true],['distinct-bound',a.release,bound,true],
  ['distinct-only-continuation',a.release,{continuationAuthorization:a.continuation},true],['distinct-only-retention',a.release,{dispositionAuthorization:a.retention},true],
  ['distinct-empty-settings',a.release,{continuationAuthorization:'',dispositionAuthorization:''},true],
  ['distinct-continuation-mismatch',a.release,{...bound,continuationAuthorization:'other-continue'},false],
  ['distinct-retention-mismatch',a.release,{...bound,dispositionAuthorization:'other-retain'},false],
  ['distinct-both-mismatch',a.release,{continuationAuthorization:'other-continue',dispositionAuthorization:'other-retain'},false],
  ['distinct-swapped-settings',a.release,{continuationAuthorization:a.retention,dispositionAuthorization:a.continuation},false],
  ['same-continuation-empty',a.continuation,{continuationAuthorization:'',dispositionAuthorization:''},false],
  ['same-retention-empty',a.retention,{continuationAuthorization:'',dispositionAuthorization:''},false],
  ['distinct-setting-equals-release',a.release,{...bound,continuationAuthorization:a.release},false],
 ]
 for(const [name,authorization,settings,allow] of cases){
  const storageName='copy-'+name,storage=join(d.root,storageName);await cp(join(d.root,'state'),storage,{recursive:true})
  const request={...release,authorization},r=await execute(d,{kind:'release',request,settings,storage});preserved(completed,r)
  if(allow){assert.deepEqual(r.outputs,['ok:released-observation-admission']);const changed=Object.keys(r.after).filter(key=>r.before[key]!==r.after[key]);assert.deepEqual(changed.sort(),['physical-observation-intent/v1','physical-observation-result/v1/release-id','physical-observation-release/v1'].map(slot).sort());const again=await execute(d,{kind:'release',request,settings:{},storage});untouched(again);assert.deepEqual(again.outputs,r.outputs)}else rejected(r)
  const startup=await s.run(d,{storageName,settings,...(allow?{frames:[line('inspect','inspect',{target:1})]}:{})});untouched(startup);preserved(original,startup);assert.equal(startup.events.some(e=>e.operation==='read-start'),allow)
  contracts.push({state,name,authorization,settings,allow,released:r.outputs[0]==='ok:released-observation-admission',startupRead:startup.events.some(e=>e.operation==='read-start')})
  if(name==='distinct-bound'){
   for(const mismatch of [{continuationAuthorization:'other-continue'},{dispositionAuthorization:'other-retain'},{continuationAuthorization:a.retention,dispositionAuthorization:a.continuation}]){
    const conflict=await execute(d,{kind:'release',request,settings:mismatch,storage});rejected(conflict);preserved(completed,conflict)
    const closed=await s.run(d,{storageName,settings:mismatch});untouched(closed);preserved(original,closed);assert.ok(!closed.events.some(e=>e.operation==='read-start'))
   }
   const reopens=await s.run(d,{storageName,settings:bound,frames:[line('inspect-again','inspect',{target:1})]});untouched(reopens);preserved(original,reopens);assert.ok(reopens.events.some(e=>e.operation==='read-start'))
  }
 }
 const storage=join(d.root,'copy-own-authority-mismatch');await cp(join(d.root,'state'),storage,{recursive:true});rejected(await execute(d,{kind:'release',request:release,settings:bound,liveAuthority:'wrong-release-authority',storage}))
 assert.deepEqual(await store(join(d.root,'state')),completed,'completed seed never released or rewritten')
}
for(const window of ['before-release-intent','after-release-intent','before-release-result','after-release-result','before-release-activation','after-release-activation']){
 const {d,accepted:a,original,completed,release}=await seed(window)
 const killed=await execute(d,{kind:'release',request:release,crash:window});preserved(completed,killed);assert.deepEqual(killed.outputs,[])
 // Reusing an accepted earlier authority cannot finish a partial release either.
 const same=await execute(d,{kind:'release',request:{...release,authorization:a.continuation}});rejected(same);preserved(completed,same)
 const mismatch=await execute(d,{kind:'release',request:release,settings:{continuationAuthorization:'relabelled'}});rejected(mismatch);preserved(completed,mismatch)
 const startup=await s.run(d);untouched(startup);preserved(original,startup);assert.equal(startup.events.some(e=>e.operation==='read-start'),window==='after-release-activation')
 const valid=await execute(d,{kind:'release',request:release});assert.deepEqual(valid.outputs,['ok:released-observation-admission']);preserved(completed,valid)
 const read=await s.run(d,{frames:[line('inspect','inspect',{target:1}),line('original-id','integrate',{target:1}),line('new-id','integrate',{target:1})]});untouched(read);preserved(original,read)
 for(const id of ['original-id','new-id'])assert.equal(read.outputs.map(JSON.parse).find(x=>x.id===id).result.outcome,'err:unresolved')
 contracts.push({window,sameAcceptedAuthorityRefused:true,currentMismatchRefused:true,validExplicitResume:true,originalUncertaintyPreserved:true})
}
// Read the old invalid activated records from a prior fresh baseline fixture.
// The revised runtime must close without rewriting/migrating any retained fact.
let legacy=0
if(process.argv[4]){
 const baseline=resolve(process.argv[4]),old=JSON.parse(await readFile(join(baseline,'raw.json')))
 for(const item of old.filter(x=>!x.contractMatches)){
  const root=join(s.out,'legacy-'+item.name);await mkdir(root);const d={root,name:'legacy-'+item.name,seq:0,scenario:item.scenario,f:{root:join(dirname(item.scenario.storageDir),'git')}}
  for(let i=0;i<2;i++){const r=await execute(d,{storage:item.scenario.storageDir});untouched(r);uncertain(r.after);assert.ok(!r.events.some(e=>e.operation==='read-start'))}
  const r=await execute(d,{kind:'release',request:item.request,storage:item.scenario.storageDir});rejected(r);uncertain(r.after);legacy++
 }
 assert.equal(legacy,3)
}
const all=[...s.rows,...rows],helpers=new Set();for(const r of all){for(const e of r.events)if(e.operation==='physical-git-owner-start')helpers.add(e.pid);for(const value of Object.values(r.after)){let w;try{w=JSON.parse(value)}catch{continue}if(w?.OwnerPid&&helpers.has(w.OwnerPid)&&w.ChildPid>0)helpers.add(w.ChildPid)}}for(const pid of helpers)assert.equal(await alive(pid),false)
await writeFile(join(s.out,'raw.json'),JSON.stringify(rows,null,2)+'\n');await writeFile(join(s.out,'application-rows.json'),JSON.stringify(s.rows,null,2)+'\n')
const proof={processes:all.length,domains:s.domains.length,legacyDomainsReused:legacy?1:0,sigkills:all.filter(r=>r.signal==='SIGKILL').length,contracts,legacyInvalidActivationsClosed:legacy,rawHash:hash(JSON.stringify(rows)),applicationRowsHash:hash(JSON.stringify(s.rows)),sources:Object.fromEntries(await Promise.all(['test-release-authority.mjs','continuation-fixture.mjs','continuation.template.cs','git-owner.mjs','compile.mjs'].map(async file=>[file,hash(await readFile(new URL(file,import.meta.url)))]))),helperObservations:[...helpers].map(pid=>({pid,running:false})),scope:'accepted request authority separation; optional current settings confirm retained lineage; controlled offline observation admission only'}
await writeFile(join(s.out,'proof.json'),JSON.stringify(proof,null,2)+'\n');console.log(`PASS: ${proof.processes} applications/${proof.domains} fresh domains/${proof.sigkills} kills; retained/current authority, replay/startup and release windows; ${legacy} old invalid activations closed`)
