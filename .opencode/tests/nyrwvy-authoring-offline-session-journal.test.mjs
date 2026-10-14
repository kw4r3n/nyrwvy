import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,cp,readFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('durable integration identity reconciles crashes and rejects conflicting payloads before retry',{timeout:600000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-journal-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/offline-session-journal/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:240000})
 try{
  run('run',bundle);run('run',join(bundle,'disabled'),'--disabled')
  run('compile',bundle);run('compile',join(bundle,'disabled'));run('test-runtime',bundle)
  await cp(bundle,moved,{recursive:true});await rm(join(moved,'runs'),{recursive:true})
  run('run','--replay',moved);run('run','--replay',join(moved,'disabled'))
  run('compile',moved);run('compile',join(moved,'disabled'));run('test-runtime',moved);run('verify',bundle,moved)
  const proof=JSON.parse(await readFile(join(bundle,'journal-proof.json'),'utf8'))
  assert.deepEqual(proof.proof.find(x=>x.name==='lost-response').total,{commit:1,head:1,save:1})
  assert.deepEqual(proof.proof.find(x=>x.name==='lost-response-disabled').total,{commit:2,head:2,save:2})
 }finally{await rm(root,{recursive:true,force:true})}
})
