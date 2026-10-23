import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,readFile,cp,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'

test('explicit exception boundary preserves dispatch, cancellation, typed abort and cleanup ownership',{timeout:180000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-exceptions-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const options={cwd:resolve('.'),encoding:'utf8',timeout:90000}
 const run=(script,...args)=>execFileSync('node',[`examples/exception-boundary/${script}.mjs`,...args],options)
 const json=async path=>JSON.parse(await readFile(path,'utf8'))
 try{
  run('run',bundle)
  const first=await json(join(bundle,'evidence.json'))
  assert.equal(first.length,4)
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved)
  assert.deepEqual(await json(join(moved,'replay-evidence.json')),first)
  for(const name of ['exceptions.nyrec','Generated.cs','symbols.json'])assert.deepEqual(await readFile(join(bundle,name)),await readFile(join(moved,name)))
  run('test-runtime',bundle)
  const real=await json(join(bundle,'runtime-evidence.json'))
  assert.equal(real.controls.length,37)
  assert.ok(real.controls.every(c=>c.passed))
  assert.equal(real.sourceUnchanged,true)
 }finally{await rm(root,{recursive:true,force:true})}
})
