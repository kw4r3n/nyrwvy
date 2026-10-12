import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join} from 'node:path'
import {projection,validate} from './cases.mjs'
const [before,after]=process.argv.slice(2),json=async p=>JSON.parse(await readFile(p,'utf8'))
const cases=await json(join(after,'cases.json')),proof=[]
const beforeCases=await json(join(before,'cases.json'))
assert.equal(cases.length,11);assert.equal(beforeCases.length,11)
for(const mode of ['virtual','task','external']){
 const rows=async dir=>mode==='virtual'?(await json(join(dir,'evidence.json'))).map(x=>x.checks):(await json(join(dir,'runtime-evidence.json'))).results.filter(x=>x.mode===mode).map(x=>x.checks)
 const b=await rows(before),a=await rows(after)
 for(const c of cases){
  const previous=b.find(x=>x.id===c.id),current=a.find(x=>x.id===c.id)
  validate(beforeCases.find(x=>x.id===c.id),previous);validate(c,current)
  // The recovered registry is the only behavioral change. Preserve every
  // request outcome, mutation trace, worker trace and release count exactly.
  const withoutView=r=>{const p=projection(r);return {...p,state:undefined,replies:p.replies.filter(x=>x.label!=='retained')}}
  assert.deepEqual(withoutView(current),withoutView(previous),`${c.id}: recovery changes only the retained view`)
  const needsHead=c.kind==='integration'&&!c.rawFault&&!c.rawHeadFault&&!c.fail
  if(needsHead){
   const head=c.patch?'integrated-head':'source-head',accept=r=>assert.match(r.state,new RegExp(`passed,${head};`))
   assert.throws(()=>accept(previous),'baseline must fail the HEAD-retention acceptance assertion');accept(current)
   proof.push({id:c.id,mode,before:previous.state,after:current.state,baselineAcceptance:'FAIL: completed HEAD absent',recoveredAcceptance:'PASS: completed HEAD retained',commit:current.events.filter(x=>x.operation==='commit').length,head:current.events.filter(x=>x.operation==='head').length,save:current.events.filter(x=>x.operation==='save').length,queuedReply:current.replies.find(x=>x.label==='second').record,released:current.released})
  }
  if(c.id==='parent-stale-reader-integration'){
   assert.ok(!current.replies.find(x=>x.label==='stale').record.includes('integrated-head'),'stale reader really observed the precommit state')
   assert.match(current.state,/integrated-head;/,'nonexclusive completed result cannot replace newer mutation state')
  }
 }
}
await writeFile(join(after,'recovery-proof.json'),JSON.stringify({scripts:cases.length,modes:['virtual','task','external'],changedBehavior:'retained immutable graph state only',proof},null,2)+'\n')
console.log('PASS: failing baseline acceptance in all three modes; completed HEAD recovery, exact outcome/effect preservation, stale reader exclusion')
