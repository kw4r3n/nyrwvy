import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join} from 'node:path'
import {acceptance,normalize} from './cases.mjs'
const [before,after]=process.argv.slice(2),json=async p=>JSON.parse(await readFile(p,'utf8'))
const cases=await json(join(after,'cases.json')),bv=await json(join(before,'evidence.json')),av=await json(join(after,process.argv.includes('--replay')?'replay-evidence.json':'evidence.json'))
const br=await json(join(before,'runtime-evidence.json')),ar=await json(join(after,'runtime-evidence.json')),oracle=await json(join(after,'oracle-evidence.json'))
const project=r=>({...r,replies:[...r.replies].sort((a,b)=>a.label.localeCompare(b.label))})
const redIds=new Set(['ordinary-raw-snapshot','held-raw-owner-cancel','admitted-close-raw-snapshot']),red=[],passed=[],preserved=[]
for(const c of cases){
 const a=acceptance(c,normalize(c,av.find(x=>x.checks.id===c.id).response)),b=normalize(c,bv.find(x=>x.checks.id===c.id).response)
 for(const [mode,rowsA,rowsB] of [['virtual',[a],[b]],...['task','external'].map(mode=>[mode,ar.results.filter(x=>x.id===c.id&&x.mode===mode).map(x=>normalize(c,x.response)),br.results.filter(x=>x.id===c.id&&x.mode===mode).map(x=>normalize(c,x.response))])]){
  assert.equal(rowsA.length,1);assert.equal(rowsB.length,1);acceptance(c,rowsA[0])
  assert.deepEqual(project(rowsA[0]),project(a),`${c.id} ${mode} exact protected observations`)
  assert.deepEqual(project(rowsB[0]),project(b),`${c.id} ${mode} exact baseline observations`)
  passed.push({id:c.id,mode})
  assert.deepEqual(rowsA[0].effects,rowsB[0].effects,'same bounded external effects, including once cleanup/release')
  if(redIds.has(c.id)){assert.throws(()=>acceptance(c,rowsB[0]));red.push({id:c.id,mode,rejected:true})}
  if(!['raw','oce'].includes(c.snapshotFault)){assert.deepEqual(project(rowsA[0]),project(rowsB[0]),'preserved typed failure/success/check fault/provider controls');preserved.push({id:c.id,mode})}
  if(c.releaseFault)assert.equal(rowsB[0].outcome,c.snapshotFault==='raw'?'error:raw snapshot fault':'error:release fault','baseline raw scope fault wins competing release; contained snapshot leaves release fault visible')
 }
 const p=oracle.results.find(x=>x.id===c.id);assert.ok(p);assert.equal(p.membersDrained,true);assert.equal(p.released,1);assert.equal(p.agentsClosed,1)
 assert.equal(p.outcome,c.releaseFault?'error:release fault':c.function?'ok':'cancelled')
 assert.equal(p.effects.filter(x=>x==='checks').length,a.effects.filter(x=>x==='checks').length)
 if(!c.checksFault&&c.snapshotFault!=='oce'){
  const state=a.replies.find(x=>x.label==='retained').record.split(',');assert.equal(state[1],p.status);assert.equal(state[6],p.commit??'');assert.equal(state[7],p.checks)
 }
 if(['raw','typed'].includes(c.snapshotFault))assert.match(p.error,/^Snapshot failed: /)
}
const realOce=br.results.filter(x=>x.id==='explicit-snapshot-oce');assert.ok(realOce.every(x=>x.response.error.exceptionType==='System.OperationCanceledException'))
const pOce=oracle.results.find(x=>x.id==='explicit-snapshot-oce');assert.equal(pOce.taskFault,'CancelledError');assert.equal(pOce.status,'completed');assert.equal(pOce.commit,null)
assert.ok(ar.results.filter(x=>x.id==='explicit-snapshot-oce').every(x=>x.response.ok))
const controls=[]
for(const [id,caseId,mutate] of [
 ['ordinary-fault-escapes','ordinary-raw-snapshot',r=>r.outcome='error:raw snapshot fault'],
 ['failed-snapshot-runs-checks','held-raw-owner-cancel',r=>r.effects.splice(-1,0,'checks')],
 ['failed-final-lost','ordinary-raw-snapshot',r=>r.replies.find(x=>x.label==='retained').record='1,completed,w1,base,0,false,commit1,passed,;'],
 ['inspection-before-join','ordinary-raw-snapshot',r=>r.replies.find(x=>x.label==='settled').record='1,running;'],
 ['duplicate-cleanup','ordinary-raw-snapshot',r=>r.effects.unshift('memberClose')],
 ['duplicate-release','ordinary-raw-snapshot',r=>r.released=2],
 ['early-release','ordinary-raw-snapshot',r=>r.effects.unshift(r.effects.pop())],
 ['release-fault-swallowed','raw-snapshot-release-precedence',r=>r.outcome='err:owner cancelled'],
 ['checks-fault-swallowed','raw-checks-fault',r=>r.outcome='ok'],
 ['close-intent-lost','successful-snapshot-close',r=>r.replies.find(x=>x.label==='@close-publication').record='refused'],
 ['success-checks-erased','successful-snapshot-checks',r=>r.effects=r.effects.filter(x=>x!=='checks')],
 ['undetermined-provider-snapshotted','undetermined-provider-cancel',r=>r.effects.splice(-1,0,'snapshot')],
 ['snapshot-token-exposed','held-raw-owner-cancel',r=>r.replies.find(x=>x.label==='@snapshot:entry').record='cancelled'],
 ['oce-divergence-hidden','explicit-snapshot-oce',r=>r.outcome='error:explicit snapshot cancellation'],
]){const c=cases.find(x=>x.id===caseId),r=structuredClone(av.find(x=>x.checks.id===caseId).checks);mutate(r);assert.throws(()=>acceptance(c,r));controls.push({id,rejected:true})}
const proof={scripts:cases.length,realRunsPerGraph:ar.results.length,exactVirtualTaskRoot:cases.length,baselineFailures:red,afterPasses:passed,preserved,controls,independentOracle:{pin:oracle.pin,sourceProofs:oracle.sourceProofs,contracts:oracle.results.length},cancellationDivergence:{realCSharp:'explicit OCE escapes before; becomes failed Final after',actualPython:'explicit CancelledError escapes snapshot catch; completed status with no commit; task cancelled',virtual:'ordinary synthetic host throw only; exception class is not measured'},wholePythonParity:false,limits:['Virtual and real share the C# emitter/runtime','ExceptionCatch catches all System.Exception including OCE; opaque Exception has no class test','Opt-in only; admission and serialized defaults unchanged','Checks and commit publication remain outside snapshot catch','Python checks RuntimeError faults its task after commit; graph checks fault propagates through owned scope','Bare owner shutdown, queued handler protection and atomic check cutoff remain separate','No live providers, physical Git recovery, unbounded interleavings or restart persistence claim']}
await writeFile(join(after,process.argv.includes('--replay')?'replay-proof.json':'proof.json'),JSON.stringify(proof,null,2)+'\n')
console.log(`PASS: ${red.length} baseline RED assertions; ${passed.length} protected passes; ${preserved.length} preserved controls; ${controls.length} record controls; ${cases.length} actual Python contracts; explicit cancellation divergence`)
