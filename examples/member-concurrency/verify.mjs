import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join} from 'node:path'
import {scenarios,projection,validate} from './cases.mjs'
const bundle=process.argv[2],json=async path=>JSON.parse(await readFile(path,'utf8'))
const expected=await json(new URL('./fixtures/oracle-expected.json',import.meta.url))
const virtual=await json(join(bundle,process.argv.includes('--replay')?'replay-evidence.json':'evidence.json'))
const real=await json(join(bundle,'runtime-evidence.json'))
assert.equal(real.sourceUnchanged,true)
assert.equal(expected.matched.length,6)
const compare=row=>assert.deepEqual(projection(row),expected.matched.find(x=>x.id===row.id),row.id)
assert.equal(virtual.length,6);assert.equal(real.results.length,6)
try{
 const oracle=await json(join(bundle,'oracle-evidence.json'))
 assert.equal(oracle.pin,expected.pin)
 assert.deepEqual(oracle.sourceProofs,expected.sourceProofs)
 assert.equal(oracle.results.length,6)
 for(const c of scenarios())compare(validate(c,oracle.results.find(r=>r.id===c.id)))
}catch(error){if(error.code!=='ENOENT')throw error}
for(const c of scenarios()){
 compare(validate(c,virtual.find(x=>x.checks.id===c.id).checks))
 compare(validate(c,real.results.find(x=>x.id===c.id).checks))
}
// Sensitivity controls invoke the same assertions as the real comparisons.
const mutations=[
 ['capacity-loser-effects','capacity-contention',r=>r.events.push({request:'loser',operation:'workspaceCreate',patch:'w3'})],
 ['queued-cancel-registered','cancel-queued-spawn',r=>r.replies.find(x=>x.label==='cancelled').record='spawn:3'],
 ['duplicate-commit','duplicate-integration-clear',r=>r.events.push({request:'second',operation:'commit',patch:''})],
 ['cancel-loses-ledger','duplicate-integration-cancel',r=>r.state=r.state.replace('integrated-head','')],
 ['wait-serialized','wait-stop-during-integration',r=>{const i=r.events.findIndex(x=>x.operation==='gate'&&x.patch==='commit-release');const e=r.events.splice(i,1)[0];r.events.unshift(e)}],
 ['close-drain-blocks-cutoff','close-cutoff-before-drain',r=>r.replies.find(x=>x.label==='late').record='spawn:3'],
 ['release-before-drain','capacity-contention',r=>r.released=2],
 ['changed-mutation-order','duplicate-integration-clear',r=>{const a=r.events.findIndex(x=>x.operation==='apply'),b=r.events.findIndex(x=>x.operation==='check');[r.events[a],r.events[b]]=[r.events[b],r.events[a]]}],
 ['premature-cancel-settlement','duplicate-integration-cancel',r=>{const i=r.events.findIndex(x=>x.operation==='reply'&&x.request==='first');r.events.unshift(r.events.splice(i,1)[0])}],
 ['early-ownership-release','duplicate-integration-clear',r=>{const i=r.events.findIndex(x=>x.operation==='gate'&&x.patch==='released:first');r.events.unshift(r.events.splice(i,1)[0])}],
]
const controls=[]
for(const [id,caseId,mutate] of mutations){
 const r=structuredClone(real.results.find(r=>r.id===caseId).checks);mutate(r)
 assert.throws(()=>{validate(scenarios().find(c=>c.id===caseId),r);compare(r)})
 controls.push({id,rejected:true})
}
await writeFile(join(bundle,'comparison.json'),JSON.stringify({matched:6,comparison:'outcomes, final state, request-local mutation order and required happens-before; no cross-request total order',controls},null,2))
console.log('PASS: six independent expected projections and ten comparison sensitivity controls')
