import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,cp,readFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('owned graph input and ordered dispatch drain Linux pipes without peer EOF and expose lost output',{timeout:600000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-owned-pipe-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/offline-session-owned-io/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:240000})
 try{
  run('run',bundle);run('run',join(bundle,'sequential'),'--sequential')
  run('compile',bundle);run('compile',join(bundle,'sequential'));run('test-runtime',bundle)
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved);run('run','--replay',join(moved,'sequential'))
  run('compile',moved);run('compile',join(moved,'sequential'));run('test-runtime',moved);run('verify',bundle,moved)
  const proof=JSON.parse(await readFile(join(bundle,'pipe-proof.json'),'utf8'))
  assert.equal(proof.proof.filter(x=>x.releaseCount===1&&!x.rejected).length,20)
  assert.deepEqual(proof.proof.filter(x=>x.rejected).map(x=>x.control),['drop-tail','wrong-id','double-close','uninterruptible-read','suppress-output-fault','sequential-reader'])
  assert.deepEqual(proof.proof.filter(x=>x.rejected).map(x=>x.contract),['response-correlation-order','response-correlation-order','resource-release-count','application-outcome','delivery-failure-observable','foreground-eof-progress'])
  const lost=proof.proof.find(x=>x.id==='broken-stdout-after-local-integration'&&!x.control)
  assert.equal(lost.localCompleted,true);assert.equal(lost.terminalWritten,false);assert.equal(lost.outputFault,'IOException')
  const stalled=proof.proof.find(x=>x.id==='stalled-stdout')
  assert.equal(stalled.localCompleted,false);assert.equal(stalled.terminalWritten,false);assert.equal(stalled.outputFault,'TimeoutException')
 }finally{await rm(root,{recursive:true,force:true})}
})
