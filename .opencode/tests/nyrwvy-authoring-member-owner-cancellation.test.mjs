import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,readFile,cp,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('owner cancellation reports after drain and retains explicit Python shutdown divergences',{timeout:180000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-owner-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/member-owner-cancellation/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:90000})
 try{
  run('run',bundle);run('test-runtime',bundle);run('verify',bundle)
  run('run','--replay',bundle);run('verify',bundle,'--replay')
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved);run('verify',moved,'--replay')
  for(const name of ['controller.json','Generated.cs','symbols.json','cases.json'])assert.deepEqual(await readFile(join(bundle,name)),await readFile(join(moved,name)))
  const comparisons=JSON.parse(await readFile(join(bundle,'comparison.json'),'utf8'))
  assert.equal(comparisons.exactPythonMatches,0);assert.equal(comparisons.controls.length,12)
 }finally{await rm(root,{recursive:true,force:true})}
})
