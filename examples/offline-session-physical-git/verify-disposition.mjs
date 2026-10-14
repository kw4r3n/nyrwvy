import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {hash} from './git-fixture.mjs'
const [original,moved,bundle,relocated]=process.argv.slice(2).map(x=>resolve(x))
const key=k=>hash(k)+'.json'
const loaded=[]
const physical=p=>({...p,index:'<index-stat-bytes>'})
for(const dir of [original,moved]){
 const raw=JSON.parse(await readFile(join(dir,'raw.json'))),proof=JSON.parse(await readFile(join(dir,'proof.json')))
 assert.equal(hash(JSON.stringify(raw)),proof.rawHash);assert.equal(proof.processes,raw.length);assert.equal(proof.sigkills,raw.filter(r=>r.signal==='SIGKILL').length);assert.equal(proof.domains,new Set(raw.map(r=>r.name)).size)
 let retained=0,rejected=0,replays=0
 for(const r of raw){
  if(!r.request||r.signal==='SIGKILL')continue
  assert.deepEqual(r.physicalBefore,r.physicalAfter,'raw physical preservation')
  const diff=Object.keys({...r.before,...r.after}).filter(k=>r.before[k]!==r.after[k])
  const ok=r.outputs[0]?.startsWith('ok:')
  if(ok){
   const q=r.request,k=key('physical-disposition/v1/'+q.id),p=JSON.parse(r.after[k]);assert.deepEqual(r.outputs,['ok:retained-unresolved:'+q.expectedState]);assert.equal(p.requestDigest,hash(JSON.stringify(q)));assert.equal(p.invocation,q.invocation);assert.equal(p.identity,q.identity);assert.equal(p.launchDigest,q.launchDigest);assert.equal(p.planDigest,q.planDigest);assert.equal(p.repository,q.repository);assert.equal(p.repositoryId,q.repositoryId);assert.equal(p.action,q.action);assert.equal(p.state,q.expectedState);assert.equal(p.originalOutcome,'not-inferred')
   assert.ok(diff.length<=1);if(diff.length){assert.deepEqual(diff,[k]);assert.equal(r.before[k],undefined);retained++}else{assert.deepEqual(r.before,r.after);replays++}
   const c=JSON.parse(r.after[key('physical-child/v1')]);assert.equal(hash(r.after[key('physical-child/v1')]),q.launchDigest);assert.equal(hash(r.after[key('physical-plan/v1/'+c.Identity)]),q.planDigest)
   if(c.State==='active'){
    const witness=r.after[key('physical-join/v1/'+c.Invocation)],w=JSON.parse(witness);assert.equal(p.witnessDigest,hash(witness));assert.equal(w.LaunchDigest,hash(r.after[key('physical-child/v1')]));assert.equal(w.Invocation,c.Invocation);assert.equal(w.Identity,c.Identity);assert.equal(w.Repository,c.Repository);assert.equal(w.RepositoryId,c.RepositoryId);assert.equal(w.OwnerPid,c.Pid);assert.equal(w.OwnerDigest,c.OwnerDigest);assert.deepEqual(w.Args,c.Args);assert.equal(w.InputDigest,c.InputDigest);assert.equal(w.InputClosed,true);assert.equal(w.OutputDrained,true)
    assert.equal(r.after[key('outcome/'+c.Identity)],undefined);assert.equal(r.after[key('outcome-receipt/'+c.Identity)],undefined)
   }
  }else{assert.deepEqual(r.outputs,['err:unresolved']);assert.deepEqual(r.before,r.after);rejected++}
  assert.ok(!r.events.some(e=>e.operation==='physical-git-exit'&&e.mutation||e.operation==='storage-write-attempt'))
 }
 // Cross-location comparisons concern semantics. Raw preservation/correlation
 // checks above remain exact. No dynamic evidence is used to infer an outcome.
 const contracts=proof.contracts.map(({ownerPid,childPid,...rest})=>rest)
 const stable=raw.map(r=>({name:r.name,index:r.index,actions:r.actions,outputs:r.outputs,code:r.code,signal:r.signal,physicalBefore:physical(r.physicalBefore),physicalAfter:physical(r.physicalAfter),storageFilesBefore:Object.keys(r.before).length,storageFilesAfter:Object.keys(r.after).length,applicationOutcomes:Object.values(r.after).filter(x=>x.startsWith('ok:')||x.startsWith('err:')).sort()}))
 loaded.push({dir,proof,contracts,stable,rawFileHash:hash(await readFile(join(dir,'raw.json'))),checks:{retained,rejected,replays}})
}
assert.deepEqual(loaded[0].contracts,loaded[1].contracts);assert.deepEqual(loaded[0].stable,loaded[1].stable);assert.deepEqual(loaded[0].checks,loaded[1].checks)
const artifacts={}
for(const prefix of ['', 'restoration-disabled/'])for(const file of ['session.json','Generated.cs','symbols.json','declarations.json','authoring.json','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','compilation.json']){
 const relative=prefix+file,left=hash(await readFile(join(bundle,relative))),right=hash(await readFile(join(relocated,relative)));assert.equal(left,right,relative);artifacts[relative]=left
}
const closure={originalMovedSemantics:true,rawLocalStorageAndPhysicalPreservation:true,rawLaunchPlanAndWitnessCorrelation:true,artifacts,processes:loaded.reduce((n,x)=>n+x.proof.processes,0),domains:loaded.reduce((n,x)=>n+x.proof.domains,0),sigkills:loaded.reduce((n,x)=>n+x.proof.sigkills,0),bundles:loaded.map(({stable,contracts,...r})=>r),normalization:['index stat-byte hash and known helper PIDs omitted only for fresh-domain semantic comparison; dynamic storage bytes compared locally, not normalized across locations'],sourceHash:hash(await readFile(new URL(import.meta.url)))}
await writeFile(join(original,'verified.json'),JSON.stringify(closure,null,2)+'\n')
console.log(`PASS: ${closure.processes} disposition processes, original/moved semantics and 18 identical artifacts`)
