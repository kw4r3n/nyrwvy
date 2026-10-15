import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,cp,readFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('compiled JSONL pipes compose EOF and cancellation with the owned offline session; wrong transport controls fail',{timeout:240000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-session-pipe-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/offline-session-transport/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:120000})
 try{
  run('run',bundle);run('compile',bundle);run('test-runtime',bundle)
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved);run('compile',moved);run('test-runtime',moved);run('verify',bundle,moved)
  const proof=JSON.parse(await readFile(join(bundle,'pipe-proof.json'),'utf8'))
  assert.equal(proof.proof.filter(x=>x.releaseCount===1).length,13)
  assert.deepEqual(proof.proof.filter(x=>x.rejected).map(x=>x.control),['drop-tail','wrong-id','double-close','uninterruptible-read'])
  assert.deepEqual(proof.proof.filter(x=>x.rejected).map(x=>x.contract),['response-correlation-order','response-correlation-order','resource-release-count','application-outcome'])
  const tail=proof.proof.find(x=>x.control==='drop-tail')
  assert.equal(tail.cause.healthyOutcome,true);assert.equal(tail.cause.missingResponseId,'tail');assert.equal(tail.cause.releaseCount,1)
  assert.deepEqual(proof.verificationGuards,[{id:'unrelated-tail-control-fault',refusedAsTailLoss:true,contract:'application-success',observedType:'NotSupportedException'}])
 }finally{await rm(root,{recursive:true,force:true})}
})
