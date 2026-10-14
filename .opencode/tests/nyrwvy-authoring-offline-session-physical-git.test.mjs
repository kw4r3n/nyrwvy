import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,cp,readFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('physical Git evidence binds the reviewed registry graph without effect-derived outcomes',{timeout:600000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-physical-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/offline-session-physical-git/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:240000})
 try{
  // Explicit retained seed is useful when the unchanged graph was already
  // independently authored/reviewed. Reopen and re-emission still run freshly.
  if(process.env.NYRWVY_PHYSICAL_GIT_SEED){
   await cp(resolve(process.env.NYRWVY_PHYSICAL_GIT_SEED),bundle,{recursive:true})
   await rm(join(bundle,'runs'),{recursive:true,force:true})
   run('run','--replay',bundle);run('run','--replay',join(bundle,'restoration-disabled'))
  }else{run('run',bundle);run('run',join(bundle,'restoration-disabled'),'--restoration-disabled')}
  run('compile',bundle);run('compile',join(bundle,'restoration-disabled'));run('test-runtime',bundle);run('test-child-lifetime',bundle,join(bundle,'child-runs'));run('test-disposition',bundle,join(bundle,'disposition-runs'))
  await cp(bundle,moved,{recursive:true});await rm(join(moved,'runs'),{recursive:true})
  await rm(join(moved,'child-runs'),{recursive:true})
  await rm(join(moved,'disposition-runs'),{recursive:true})
  run('run','--replay',moved);run('run','--replay',join(moved,'restoration-disabled'))
  run('compile',moved);run('compile',join(moved,'restoration-disabled'));run('test-runtime',moved);run('test-child-lifetime',moved,join(moved,'child-runs'));run('test-disposition',moved,join(moved,'disposition-runs'));run('verify',bundle,moved);run('verify-child-lifetime',join(bundle,'child-runs'),join(moved,'child-runs'));run('verify-disposition',join(bundle,'disposition-runs'),join(moved,'disposition-runs'),bundle,moved)
  const {proof}=JSON.parse(await readFile(join(bundle,'physical-proof.json'),'utf8'))
  assert.deepEqual(proof.find(x=>x.name==='restoration').total,{'commit-tree':1,'update-ref':1,'read-tree':1})
  assert.equal(proof.find(x=>x.name==='receipt-only-control').duplicateGitInvocation,true)
  assert.equal(proof.find(x=>x.name==='graph-control').restoredHeadContractRejected,true)
  assert.equal(proof.find(x=>x.name==='cancel-before-registry-publication').sameId,'err:unresolved')
  assert.equal(proof.find(x=>x.name==='cancel-before-outcome-receipt').sameId,'err:cancelled')
  run('test-owner-failures',join(root,'owner-failures'))
  for(const boundary of ['pending','ready']){
   assert.equal(proof.find(x=>x.name===`${boundary}-executable-mode`).contractRejected,false)
   assert.equal(proof.find(x=>x.name===`${boundary}-executable-mode-content-only-control`).contractRejected,true)
  }
 }finally{await rm(root,{recursive:true,force:true})}
})
