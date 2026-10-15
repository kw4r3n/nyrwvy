import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,cp,readFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('raw JSON-RPC composes the maintained offline session; rejects invalid dispatch and incorrect ID controls',{timeout:240000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-leader-wire-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/offline-leader-jsonrpc/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:120000})
 try{
  run('run',bundle);run('test-runtime',bundle)
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved);run('test-runtime',moved);run('verify',bundle,moved)
  const proof=JSON.parse(await readFile(join(bundle,'proof.json'),'utf8'))
  assert.equal(proof.movedReplay,true);assert.equal(proof.historicalProbe9Recovered,false)
  assert.equal(proof.controls.filter(x=>x.terminal).length,2)
  for(const name of ['compiled-wire-parser','compiled-wire-validation','compiled-command-boundary'])assert.deepEqual(proof.controls.filter(x=>x.id===name).map(x=>x.rejected),[false,true])
 }finally{await rm(root,{recursive:true,force:true})}
})
