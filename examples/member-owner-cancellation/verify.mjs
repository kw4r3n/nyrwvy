import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join} from 'node:path'
import {normalize,projection,validate} from './cases.mjs'
const dir=process.argv[2],replay=process.argv.includes('--replay'),json=async p=>JSON.parse(await readFile(p,'utf8'))
const cases=await json(join(dir,'cases.json')),adapted=cases[0].adapted
const expected=await json(new URL(`./fixtures/nyrwvy-${adapted?'adapted':'baseline'}.json`,import.meta.url)),python=await json(new URL('./fixtures/oracle-expected.json',import.meta.url))
const virtual=await json(join(dir,replay?'replay-evidence.json':'evidence.json')),real=await json(join(dir,'runtime-evidence.json'))
const at=(row,operation,patch)=>row.events.findIndex(e=>e.operation===operation&&e.patch===patch)
function validatePython(c,row){
 assert.equal(row.outcome,c.releaseFault?'error:release fault':'cancelled')
 assert.equal(row.released,1);assert.equal(row.leaderClosed,true);assert.equal(row.handlersDrained,true);assert.equal(row.membersDrained,true)
 const replies=Object.fromEntries(row.replies.map(x=>[x.label,x.record]))
 assert.equal(replies.late,'reject:closing');assert.ok(at(row,'reply','reject:closing')<at(row,'gate','shutdown-release'))
 assert.equal(row.events.filter(e=>e.operation==='workspaceCreate'&&e.request==='late').length,0)
 for(const operations of Object.values(row.workers))assert.equal(operations.filter(x=>x==='memberClose').length,1)
 if(c.kind==='queued'){assert.equal(replies.loser,'reject:capacity');assert.match(row.state,/2,stopped,w2/)}
 if(c.kind==='integration'){
  assert.equal(replies.first,c.rawFault?'err:raw commit fault':'ok:integrated-head');assert.equal(replies.second,'ok:integrated-head');assert.match(row.state,/passed,integrated-head;/)
  const first=row.events.findIndex(e=>e.operation==='reply'&&e.request==='first'),tail=c.rawFault?'reverse-apply':'save'
  const end=row.events.findIndex(e=>e.operation===tail);assert.ok(end>=0&&first>end)
 }
 if(c.kind==='settlement')assert.match(row.state,/1,completed,w1,base,0,false,commit1,,;/)
 assert.deepEqual(projection(row),projection(python.results.find(x=>x.id===c.id)))
 return row
}
assert.equal(real.sourceUnchanged,true);assert.equal(virtual.length,6);assert.equal(real.results.length,12)
const normalized=[]
for(const c of cases){
 const v=virtual.find(x=>x.checks.id===c.id),vn=validate(c,normalize(c,v.response.ok?v.response.result.calls:v.response.error.calls,v.checks.outcome))
 assert.deepEqual(projection(vn),expected.find(x=>x.id===c.id))
 for(const mode of ['task','external']){
  const r=real.results.find(x=>x.id===c.id&&x.mode===mode)
  assert.equal(r.Opened,1);assert.equal(r.Closed,1)
  const n=validate(c,normalize(c,r.response.ok?r.response.result.calls:r.response.error.calls,r.checks.outcome))
  assert.deepEqual(projection(n),projection(vn),`${c.id}: ${mode} and virtual exact outcomes`)
  normalized.push({...n,mode})
 }
 const p=validatePython(c,python.results.find(x=>x.id===c.id))
 assert.notDeepEqual(projection(vn),projection(p),'fixture records divergence; no full Python parity claim')
}
try{
 const actual=await json(join(dir,'oracle-evidence.json'))
 assert.equal(actual.pin,python.pin);assert.deepEqual(actual.sourceProofs,python.sourceProofs)
 for(const c of cases){const row=validatePython(c,actual.results.find(x=>x.id===c.id));assert.deepEqual(row.diagnostics,python.results.find(x=>x.id===c.id).diagnostics)}
}catch(error){if(error.code!=='ENOENT')throw error}
if(adapted){
 const baseline=await json(new URL('./fixtures/nyrwvy-baseline.json',import.meta.url))
 for(const row of expected){const before=baseline.find(x=>x.id===row.id);assert.deepEqual({...row,outcome:before.outcome},before,'only the owner return contract changes')}
 const control=await json(join(dir,replay?'replay-control.json':'control.json'));assert.equal(control.response.result.actual.isOk,true)
}
const controls=[],mutations=[
 ['owner-cancellation-hidden','parent-queued-admission',r=>r.outcome=adapted?'ok':'err:owner cancelled'],
 ['queued-admission-after-cancel','parent-queued-admission',r=>r.replies.find(x=>x.label==='loser').record='spawn:3'],
 ['duplicate-release','parent-shutdown-clear',r=>r.released=2],
 ['early-resource-release','parent-protected-integration-clear',r=>{const i=r.events.findIndex(x=>x.operation==='release');r.events.unshift(r.events.splice(i,1)[0])}],
 ['lost-protected-tail','parent-protected-integration-clear',r=>r.events=r.events.filter(x=>x.operation!=='save')],
 ['lost-rollback','parent-protected-integration-fault',r=>r.events=r.events.filter(x=>x.operation!=='reverse-apply')],
 ['swallowed-cleanup-fault','parent-shutdown-fault',r=>r.outcome='err:owner cancelled'],
 ['invented-late-ledger','parent-protected-integration-clear',r=>r.state=r.state.replace('passed,;','passed,integrated-head;')],
]
for(const [id,caseId,mutate] of mutations){const c=cases.find(x=>x.id===caseId),r=structuredClone(normalized.find(x=>x.id===caseId));mutate(r);assert.throws(()=>{validate(c,r);assert.deepEqual(projection(r),expected.find(x=>x.id===caseId))});controls.push({id,rejected:true})}
for(const [id,caseId,mutate] of [
 ['python-lost-ledger','parent-protected-integration-clear',r=>r.state=r.state.replace('integrated-head','')],
 ['python-late-admission','parent-shutdown-clear',r=>r.replies.find(x=>x.label==='late').record='spawn:3'],
 ['python-determined-cancelled','parent-held-member-settlement',r=>r.state=r.state.replace('1,completed','1,stopped')],
 ['python-release-fault-loses-cancel','parent-shutdown-fault',r=>r.outcome='cancelled'],
]){const r=structuredClone(python.results.find(x=>x.id===caseId));mutate(r);assert.throws(()=>validatePython(cases.find(x=>x.id===caseId),r));controls.push({id,rejected:true})}
await writeFile(join(dir,replay?'replay-comparison.json':'comparison.json'),JSON.stringify({scripts:6,realRuns:12,exactVirtualReal:6,exactPythonMatches:0,adapted,controls,pythonPin:python.pin,comparison:'Exact request outcomes, observable registry views, per-request mutation order, Member effect order and required happens-before. Independent cross-request total order is not required. Python shutdown policy remains distinct.'},null,2)+'\n')
console.log('PASS: six exact virtual/task/root records; six independently asserted Python divergences; twelve sensitivity controls')
