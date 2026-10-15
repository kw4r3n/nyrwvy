import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,readFile,cp,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
const projection=row=>({id:row.id,observations:row.observations,steps:row.steps,replies:row.replies.filter(x=>!x.label.endsWith(':state')).sort((a,b)=>a.label<b.label?-1:a.label>b.label?1:0)})

test('serialized Member controller composes ledger, observation, shield and drainage', {timeout:180000}, async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-controller-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const options={cwd:resolve('.'),encoding:'utf8',timeout:90000}
 const run=(script,...args)=>execFileSync('node',[`examples/member-controller/${script}.mjs`,...args],options)
 const json=async path=>JSON.parse(await readFile(path,'utf8'))
 try{
  run('run',bundle)
  const first=await json(join(bundle,'evidence.json'))
  const expected=await json(new URL('../../examples/member-controller/fixtures/oracle-expected.json',import.meta.url))
  const compare=rows=>{
   assert.equal(rows.length,expected.matched.length,'every scenario has an independent expected observation')
   for(const e of expected.matched)assert.deepEqual(projection(rows.find(r=>r.id===e.id)),e,e.id)

  }
  compare(first.map(r=>r.checks))
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved)
  compare((await json(join(moved,'replay-evidence.json'))).map(r=>r.checks))
  for(const name of ['controller.json','Generated.cs','symbols.json'])assert.deepEqual(await readFile(join(bundle,name)),await readFile(join(moved,name)))
  run('test-runtime',bundle)
  const runtime=await json(join(bundle,'runtime-evidence.json'))
  compare(runtime.results.map(r=>r.checks))
  assert.equal(runtime.sourceUnchanged,true)
  assert.ok(runtime.results.every(r=>r.Opened===1&&r.Closed===1))
 }finally{await rm(root,{recursive:true,force:true})}
})
