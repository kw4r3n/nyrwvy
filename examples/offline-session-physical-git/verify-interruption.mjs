// Audit raw bytes and accepted authority/custody/state independently of the
// test assertions. Cross-domain comparison omits only raw index-stat digest.
import assert from 'node:assert/strict'
import {readFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {hash} from './git-fixture.mjs'
import {slot} from './continuation-fixture.mjs'
import {interruptionKey,custody,storageContract} from './interruption-fixture.mjs'
const dirs=process.argv.slice(2,4).map(x=>resolve(x)),bundles=process.argv.slice(4,6).map(x=>resolve(x)),json=async p=>JSON.parse(await readFile(p)),sha=async p=>hash(await readFile(p)),audits=[]
const opaque=x=>typeof x==='string'&&x.length>0,id=x=>typeof x==='string'&&/^[a-zA-Z0-9_-]{1,80}$(?![\s\S])/.test(x)
const physical=p=>Object.fromEntries(Object.entries(p).filter(([key])=>key!=='index'))
for(const dir of dirs){
 const rows=await json(join(dir,'raw.json')),proof=await json(join(dir,'proof.json'));assert.equal(proof.rawHash,hash(JSON.stringify(rows)));assert.equal(proof.processes,rows.length)
 for(const [file,digest] of Object.entries(proof.sources))assert.equal(await sha(new URL(file,import.meta.url)),digest,file)
 let retained=0,refusals=0,replays=0,preservedHashes=0,prelaunch=0,custodyRows=0
 const semantics=[]
 for(const r of rows){
  const admin=Object.hasOwn(r.scenario,'interruptedContinuationRequest'),read=r.events.some(e=>e.operation==='read-start')
  if(admin){
   assert.equal(read,false);assert.deepEqual(r.physicalBefore,r.physicalAfter);assert.ok(!r.events.some(e=>e.operation==='physical-git-owner-start'||e.operation==='storage-write-attempt'))
   const changed=Object.keys(r.after).filter(k=>r.before[k]!==r.after[k]);for(const [k,v] of Object.entries(r.before)){assert.equal(r.after[k],v,k);preservedHashes++}
   if(r.outputs[0]==='err:unresolved'){assert.deepEqual(r.before,r.after);refusals++}
   else{
    assert.ok(r.outputs.length===0&&r.signal==='SIGKILL'||/^ok:retained-unresolved-continuation:/.test(r.outputs[0]));assert.ok(changed.length===0||JSON.stringify(changed)===JSON.stringify([slot(interruptionKey)]))
    if(!r.after[slot(interruptionKey)]){assert.equal(r.outputs.length,0);assert.equal(r.scenario.crashWindow,'before-interrupted-disposition-publication');continue}
    const p=JSON.parse(r.after[slot(interruptionKey)]),raw=typeof r.request==='string'?r.request:JSON.stringify(r.request),q=JSON.parse(raw),intentRaw=r.after[slot('physical-continuation-intent/v1')],intent=JSON.parse(intentRaw),c=JSON.parse(intent.Request),t=JSON.parse(c.retentionRequest),current=r.scenario.interruptedContinuationAuthorization
    assert.ok(id(q.id)&&id(c.id)&&id(t.id));assert.ok(q.id!==c.id&&q.id!==t.id&&q.id!==intent.Identity);assert.ok(opaque(q.authorization)&&opaque(c.authorization)&&opaque(t.authorization)&&opaque(current));assert.equal(q.authorization,current);assert.notEqual(q.authorization,c.authorization);assert.notEqual(q.authorization,t.authorization);assert.notEqual(c.authorization,t.authorization)
    for(const [setting,accepted] of [['dispositionAuthorization',t.authorization],['continuationAuthorization',c.authorization]])if(Object.hasOwn(r.scenario,setting)){assert.equal(typeof r.scenario[setting],'string');assert.ok(r.scenario[setting]===''||r.scenario[setting]===accepted)}
    assert.equal(q.storageContract,storageContract);assert.equal(r.scenario.interruptedContinuationStorageContract,storageContract);assert.equal(p.request,raw);assert.equal(p.requestDigest,hash(raw));assert.equal(p.pendingDigest,hash(intentRaw));assert.equal(q.continuationIntentDigest,p.pendingDigest);assert.equal(q.continuationRequest,intent.Request);assert.equal(q.continuationId,intent.Id);assert.equal(p.continuationId,intent.Id);assert.equal(p.continuationRequestDigest,hash(intent.Request));assert.equal(p.originalOutcome,'not-inferred');assert.equal(p.continuationOutcome,'not-inferred');assert.equal(p.admission,'closed')
    assert.equal(r.after[slot('physical-continuation-result/v1/'+intent.Id)],undefined);assert.equal(r.after[slot('outcome/'+intent.Identity)],undefined);assert.equal(r.after[slot('outcome-receipt/'+intent.Identity)],undefined);assert.equal(r.after[slot('physical-observation-release/v1')],undefined)
    const evidence=Object.fromEntries(Object.entries(r.after).filter(([k])=>k!==slot(interruptionKey)).map(([k,v])=>[k,hash(v)]));assert.deepEqual(p.evidence,evidence);assert.equal(p.evidenceDigest,hash(JSON.stringify(evidence)));assert.equal(q.evidenceDigest,p.evidenceDigest)
    for(const [key,digest] of Object.entries(intent.Originals))assert.equal(evidence[key],digest,key)
    const stages=custody(r.after,intent);assert.deepEqual(p.custody,stages);assert.equal(p.custodyDigest,hash(JSON.stringify(stages)));assert.equal(q.custodyDigest,p.custodyDigest);custodyRows+=stages.length
    for(const stage of stages)if(stage.status!=='absent'){
     const child=JSON.parse(r.after[slot(stage.key)]),w=JSON.parse(r.after[slot('physical-join/v1/'+child.Invocation)]);assert.equal(child.Version,'2');assert.ok(child.Pid>0&&w.ChildPid>0);assert.equal(w.OwnerPid,child.Pid);assert.equal(w.OwnerDigest,child.OwnerDigest);assert.equal(w.InputClosed,true);assert.equal(w.OutputDrained,true);assert.equal(w.OutputDigest,hash(''));assert.equal(w.ErrorDigest,hash(''));assert.deepEqual(w.Args,intent.Commands[stage.stage]);assert.equal(w.InputDigest,hash(''));const active={...child,State:'active',ExitCode:null,InputClosed:false,OutputDrained:false};assert.equal(w.LaunchDigest,hash(child.State==='active'?r.after[slot(stage.key)]:JSON.stringify(active)))
    }
    const plan=JSON.parse(r.after[slot('physical-plan/v1/'+intent.Identity)]),physical=r.physicalAfter,facts={Head:physical.head,Symbolic:plan.Before.Symbolic,Refs:physical.refs,Objects:physical.objects.join('\n'),IndexBytes:physical.index,IndexEntries:p.state==='applied'?plan.ExpectedIndex:plan.Before.IndexEntries,Worktree:'file.txt:'+physical.gitMode+':'+hash(physical.worktree)}
    assert.equal(p.stateDigest,hash(JSON.stringify(facts)));assert.equal(q.observedStateDigest,p.stateDigest);assert.equal(q.expectedState,p.state);assert.equal(p.result,'ok:retained-unresolved-continuation:'+p.state)
    if(changed.length)retained++;else replays++
   }
  }
  if(r.name.startsWith('prelaunch-')&&admin){const intent=JSON.parse(r.before[slot('physical-continuation-intent/v1')]),child=JSON.parse(r.before[slot('physical-continuation-child/v1/'+intent.Id+'/0')]);assert.equal(child.Pid,null);assert.equal(child.State,'active');assert.deepEqual(r.outputs,['err:unresolved']);prelaunch++}
  semantics.push({name:r.name,index:r.index,admin,actions:r.actions,outputs:r.outputs,code:r.code,signal:r.signal,read,changed:Object.keys(r.after).filter(k=>r.before[k]!==r.after[k]).length,physicalBefore:physical(r.physicalBefore),physicalAfter:physical(r.physicalAfter)})
 }
 assert.equal(prelaunch,2);audits.push({processes:rows.length,retained,refusals,replays,preservedHashes,custodyRows,semantics,contracts:proof.contracts})
}
assert.deepEqual(audits[0].semantics,audits[1].semantics);assert.deepEqual(audits[0].contracts,audits[1].contracts)
const files=['session.json','Generated.cs','symbols.json','declarations.json','authoring.json','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','compilation.json'];let artifacts=0
for(const prefix of ['','restoration-disabled/'])for(const file of files){assert.equal(await sha(join(bundles[0],prefix+file)),await sha(join(bundles[1],prefix+file)),prefix+file);artifacts++}
console.log(JSON.stringify({verdict:'passed',sets:audits.map(({semantics,contracts,...x})=>x),matchingArtifacts:artifacts,crossDomainIndexStatDigestExcluded:true,scope:'separate disposition only; original/new missing outcomes, records and closed admission preserved'},null,2))
