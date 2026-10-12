import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,readFile,cp,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('bounded mutation admission leaves wait/stop and closing cutoff independent',{timeout:180000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-concurrency-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/member-concurrency/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:90000})
 try{
  run('run',bundle)
  run('test-runtime',bundle)
  run('verify',bundle)
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved)
  run('verify',moved,'--replay')
  for(const name of ['controller.json','Generated.cs','symbols.json','cases.json'])assert.deepEqual(await readFile(join(bundle,name)),await readFile(join(moved,name)))
 }finally{await rm(root,{recursive:true,force:true})}
})
