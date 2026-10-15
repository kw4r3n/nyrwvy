import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,cp,readFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('reader faults propagate during held foreground work with protected mutation and causal controls',{timeout:600000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-reader-propagation-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/offline-session-reader-propagation/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:240000})
 try{
  run('run',bundle);run('run',join(bundle,'deferred'),'--deferred')
  run('compile',bundle);run('compile',join(bundle,'deferred'));run('test-runtime',bundle);run('probe-restart',bundle)
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved);run('run','--replay',join(moved,'deferred'))
  run('compile',moved);run('compile',join(moved,'deferred'));run('test-runtime',moved);run('probe-restart',moved);run('verify',bundle,moved)
  const proof=JSON.parse(await readFile(join(bundle,'pipe-proof.json'),'utf8'))
  assert.equal(proof.proof.filter(x=>!x.rejected).length,8);assert.equal(proof.proof.filter(x=>x.rejected).length,2)
  const gap=JSON.parse(await readFile(join(bundle,'restart-proof.json'),'utf8'))
  assert.deepEqual(gap.repeatedEffects,{commit:2,head:2,save:2})
 }finally{await rm(root,{recursive:true,force:true})}
})
