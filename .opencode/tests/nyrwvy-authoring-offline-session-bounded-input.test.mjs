import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,cp,readFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('graph byte policy bounds input before decoding and preserves owned pipe cleanup',{timeout:600000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-bounded-input-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/offline-session-bounded-input/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:240000})
 try{
  run('run',bundle);run('run',join(bundle,'relaxed'),'--relaxed')
  run('compile',bundle);run('compile',join(bundle,'relaxed'));run('test-runtime',bundle)
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved);run('run','--replay',join(moved,'relaxed'))
  run('compile',moved);run('compile',join(moved,'relaxed'));run('test-runtime',moved);run('verify',bundle,moved)
  const proof=JSON.parse(await readFile(join(bundle,'pipe-proof.json'),'utf8'))
  assert.equal(proof.limitBytes,16384)
  assert.equal(proof.proof.filter(x=>x.releaseCount===1&&!x.rejected).length,20)
  assert.deepEqual(proof.proof.filter(x=>x.rejected).map(x=>[x.id,x.control]),[['oversize-lf','relaxed-policy'],['oversize-unterminated','relaxed-policy'],['one-mib-unterminated','relaxed-policy']])
  assert.equal(proof.proof.find(x=>x.id==='foreground-reader-fault-deferred').foregroundFaultDeferred,true)
 }finally{await rm(root,{recursive:true,force:true})}
})
