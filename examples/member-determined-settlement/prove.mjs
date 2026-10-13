import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join} from 'node:path'
import {acceptance,normalize} from './cases.mjs'
const [before,after]=process.argv.slice(2),json=async p=>JSON.parse(await readFile(p,'utf8'))
const cases=await json(join(after,'cases.json')),bv=await json(join(before,'evidence.json')),av=await json(join(after,process.argv.includes('--replay')?'replay-evidence.json':'evidence.json'))
const br=await json(join(before,'runtime-evidence.json')),ar=await json(join(after,'runtime-evidence.json'))
const oracle=await json(join(after,'oracle-evidence.json'))
const project=r=>({...r,replies:[...r.replies].sort((a,b)=>a.label.localeCompare(b.label))})
const redIds=new Set(['determined-snapshot','determined-typed-snapshot','determined-cleanup','determined-failed-cleanup','determined-release-fault']),red=[],passed=[]
for(const c of cases){
 const a=acceptance(c,normalize(c,av.find(x=>x.checks.id===c.id).response)),b=normalize(c,bv.find(x=>x.checks.id===c.id).response)
 for(const [mode,rowsA,rowsB] of [['virtual',[a],[b]],...['task','external'].map(mode=>[mode,ar.results.filter(x=>x.id===c.id&&x.mode===mode).map(x=>normalize(c,x.response)),br.results.filter(x=>x.id===c.id&&x.mode===mode).map(x=>normalize(c,x.response))])]){
  assert.equal(rowsA.length,1);assert.equal(rowsB.length,1)
  acceptance(c,rowsA[0]);assert.deepEqual(project(rowsA[0]),project(a),`${c.id} ${mode} exact graph observations`);passed.push({id:c.id,mode})
  if(redIds.has(c.id)){assert.throws(()=>acceptance(c,rowsB[0]));red.push({id:c.id,mode,rejected:true})}
 }
 const p=oracle.results.find(x=>x.id===c.id);assert.ok(p);assert.equal(p.membersDrained,true);assert.equal(p.released,1);assert.equal(p.outcome,c.releaseFault?'error:release fault':'cancelled')
 // Python catches snapshot failures and suppresses checks after closing.
 // Compare only the explicit bounded settlement contract, retain all records.
 if(!c.snapshotFault?.includes('raw')){
  const state=a.replies.find(x=>x.label==='retained').record.split(',');assert.equal(state[1],p.status);assert.equal(state[6],p.commit??'')
 }
}
const controls=[]
for(const [id,caseId,mutate] of [
 ['snapshot-token-exposed','determined-snapshot',r=>r.replies.find(x=>x.label==='@snapshot').record='cancelled'],
 ['cleanup-downgrades-completion','determined-cleanup',r=>r.replies.find(x=>x.label==='retained').record=r.replies.find(x=>x.label==='retained').record.replace('completed','stopped')],
 ['undetermined-completion-retained','cancel-before-completed-provider-return',r=>r.replies.find(x=>x.label==='retained').record=r.replies.find(x=>x.label==='retained').record.replace('stopped','completed')],
 ['duplicate-cleanup','determined-snapshot',r=>r.effects.unshift('memberClose')],
 ['raw-fault-loses-cancellation','determined-raw-snapshot',r=>r.outcome='err:owner cancelled'],
 ['release-fault-loses-cancellation','determined-release-fault',r=>r.outcome='err:owner cancelled'],
 ['early-release','determined-snapshot',r=>r.effects.unshift(r.effects.pop())],
 ['prior-completion-hidden','settled-before-cancel',r=>r.replies.find(x=>x.label==='before-cancel').record='1,running;'],
]){const c=cases.find(x=>x.id===caseId),r=structuredClone(av.find(x=>x.checks.id===caseId).checks);mutate(r);assert.throws(()=>acceptance(c,r));controls.push({id,rejected:true})}
const proof={scripts:cases.length,realRunsPerGraph:ar.results.length,exactVirtualTaskRoot:cases.length,baselineFailures:red,afterPasses:passed,controls,independentOracle:{pin:oracle.pin,sourceProofs:oracle.sourceProofs,contracts:oracle.results.length},wholePythonParity:false,limits:['Virtual and real share the C# emitter/runtime','C# raw snapshot faults propagate; Python records failed snapshot','Python close suppresses checks; cancelled graph closing handoff remains closed','Python entry may acquire an agent before provider run; graph pre-entry creates none','Queued mutation protection remains separate; retained matrices verify independent request cancellation']}
await writeFile(join(after,process.argv.includes('--replay')?'replay-proof.json':'proof.json'),JSON.stringify(proof,null,2)+'\n')
console.log(`PASS: ${red.length} baseline acceptance failures; ${passed.length} protected passes; ${controls.length} record controls; ${cases.length} actual Python contracts`)
