// Real schedule controls for a pre-existing harness assertion race. All variants
// execute the same unedited Generated.cs; only request scripts and host gates vary.
import assert from 'node:assert/strict'
import {cp,readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {argumentsFor,arg,projection,validate} from './cases.mjs'
import {request} from '../member-lifecycle/cases.mjs'
const bundle=resolve(process.argv[2]),output=resolve(process.argv[3])
const json=async p=>JSON.parse(await readFile(p,'utf8'))
const cases=await json(join(bundle,'cases.json'))
const virtual=await json(join(bundle,'evidence.json'))
const hash=x=>createHash('sha256').update(x).digest('hex')
const sourceHash=hash(await readFile(join(bundle,'Generated.cs')))
const controls=[]
for(const schedule of ['retained','observed','early']){
 const dir=join(output,schedule)
 await cp(bundle,dir,{recursive:true,errorOnExist:true,force:false})
 const changed=cases.map(c=>{
  if(c.kind!=='integration')return c
  const script=c.script.flatMap(r=>r.label==='settled-b'?
   schedule==='retained'?[]:schedule==='observed'?[request('inspect','@prejoin',2),r]:[r]:[r])
  const next={...c,script};return {...next,arguments:argumentsFor(next)}
 })
 await writeFile(join(dir,'cases.json'),JSON.stringify(changed,null,2)+'\n')
 const stdout=execFileSync('node',['examples/member-owner-cancellation/test-runtime.mjs',dir,schedule],{cwd:resolve('.'),encoding:'utf8',timeout:90000})
 await writeFile(join(dir,'runtime.log'),stdout)
 const real=await json(join(dir,'runtime-evidence.json'))
 assert.equal(real.generatedSha256,sourceHash);assert.equal(real.sourceUnchanged,true)
 for(const c of changed.filter(c=>c.kind==='integration'))for(const mode of ['task','external']){
  const r=real.results.find(r=>r.id===c.id&&r.mode===mode)
  const expected=projection(virtual.find(v=>v.checks.id===c.id).checks)
  const actual=projection(r.checks)
  assert.equal(r.Opened,1);assert.equal(r.Closed,1)
  assert.deepEqual(r.checks.workers['2'],['memberClose'],'peer cleanup occurs exactly once even in RED')
  if(schedule==='retained'){
   // This is the exact old expectation: no settled-b reply, terminal peer state.
   expected.replies=expected.replies.filter(r=>r.label!=='settled-b')
   assert.match(actual.state,/2,running/,'held provider forces a legitimate pre-completion observation')
   assert.throws(()=>assert.deepEqual(actual,expected),{name:'AssertionError'},'old exact terminal comparison is RED')
   // Establish that the sole old difference is the pre-completion peer view.
   const terminal=expected.state.match(/2,stopped[^;]*;/)[0]
   const corrected=structuredClone(actual)
   corrected.state=corrected.state.replace(/2,running[^;]*;/,terminal)
   corrected.replies.find(r=>r.label==='retained').record=corrected.state
   assert.deepEqual(corrected,expected,'RED preserves outcomes, mutation tail, cleanup, drain and release')
  }else{
   assert.deepEqual(actual,expected,'joined final view remains exact under alternate scheduling')
   if(schedule==='observed'){
    const calls=r.response.ok?r.response.result.calls:r.response.error.calls
    const observation=calls.find(x=>x.operation==='reply'&&arg(x,'label')==='@prejoin')
    assert.equal(arg(observation,'record'),'2,running,w2,base,0,false,,,;','pre-completion observation remains distinct from final retained state')
   }
   const pending=structuredClone(r.checks)
   pending.state=pending.state.replace(/2,stopped[^;]*;/,'2,running,w2,base,0,false,,,;')
   assert.throws(()=>validate(c,pending),'terminal assertion still rejects a pre-completion retained state')
  }
  controls.push({id:c.id,mode,schedule,result:schedule==='retained'?'RED: old exact comparison rejected':'GREEN: exact terminal comparison',preCompletion:schedule==='retained'||schedule==='observed',released:r.checks.released,peerCleanup:r.checks.workers['2'],retained:actual.state})
 }
}
await writeFile(join(output,'prejoin-proof.json'),JSON.stringify({generatedSha256:sourceHash,sourceUnchanged:true,red:controls.filter(c=>c.schedule==='retained').length,green:controls.filter(c=>c.schedule!=='retained').length,controls},null,2)+'\n')
console.log(`PASS: deterministic old RED in both modes; pre-completion and alternate joined GREEN (${controls.length} controls)`)
