import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,cp,readFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('durable registry restores owned Members and HEAD before distinct-ID integration admission',{timeout:600000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-registry-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/offline-session-registry/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:240000})
 try{
  run('run',bundle);run('run',join(bundle,'restoration-disabled'),'--restoration-disabled')
  run('compile',bundle);run('compile',join(bundle,'restoration-disabled'));run('test-runtime',bundle)
  await cp(bundle,moved,{recursive:true});await rm(join(moved,'runs'),{recursive:true})
  run('run','--replay',moved);run('run','--replay',join(moved,'restoration-disabled'))
  run('compile',moved);run('compile',join(moved,'restoration-disabled'));run('test-runtime',moved);run('verify',bundle,moved)
  const {proof}=JSON.parse(await readFile(join(bundle,'registry-proof.json'),'utf8'))
  assert.deepEqual(proof.find(x=>x.name==='restoration').total,{commit:1,head:1,save:1})
  assert.deepEqual(proof.find(x=>x.name==='restoration-disabled').total,{commit:2,head:2,save:2})
  assert.equal(proof.find(x=>x.name==='cancel-before-registry-publication').sameId,'err:unresolved')
  assert.equal(proof.find(x=>x.name==='cancel-before-outcome-receipt').sameId,'err:cancelled')
 }finally{await rm(root,{recursive:true,force:true})}
})
