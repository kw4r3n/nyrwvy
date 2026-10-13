import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,cp,readFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'

test('offline Leader turns compose admission, Member defaults, integration, cancellation outcomes and joined shutdown',{timeout:180000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-leader-team-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/offline-leader-team/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:90000})
 try{
  run('run',bundle);run('test-runtime',bundle)
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved);run('test-runtime',moved);run('verify',bundle,moved)
  const proof=JSON.parse(await readFile(join(bundle,'proof.json'),'utf8'))
  assert.equal(proof.movedReplay,true)
  assert.equal(proof.controls.filter(x=>x.terminal).length,2)
  assert.equal(proof.controls.filter(x=>x.identityPreserved).length,5)
 }finally{await rm(root,{recursive:true,force:true})}
})
