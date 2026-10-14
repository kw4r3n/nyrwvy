import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {createHash} from 'node:crypto'
const dirs=process.argv.slice(2).map(x=>resolve(x)),hash=x=>createHash('sha256').update(x).digest('hex')
assert.equal(dirs.length,2)
const proofs=[],rows=[],restarts=[]
for(const dir of dirs){
 const p=JSON.parse(await readFile(join(dir,'pipe-proof.json'),'utf8'))
 for(const [file,h] of Object.entries(p.hashes))assert.equal(hash(await readFile(join(dir,file))),h,file)
 for(const [file,h] of Object.entries(p.sourceHashes))assert.equal(hash(await readFile(resolve(file))),h,file)
 const raw=JSON.parse(await readFile(join(dir,'pipe-raw.json'),'utf8'))
 assert.equal(raw.length,11);assert.equal(new Set(raw.map(x=>x.id+'/'+x.deferred+'/'+x.control)).size,11)
 assert.equal(p.proof.filter(x=>!x.rejected).length,8);assert.equal(p.proof.filter(x=>x.rejected).length,2);assert.equal(p.guards.length,1)
 for(const row of raw){
  const offered=row.actions.filter(x=>x.writeHex).map(x=>x.writeHex).join('')
  const read=row.events.filter(x=>x.operation==='pipe-read').map(x=>x.hex.toLowerCase()).join('')
  assert.ok(offered.startsWith(read),row.id)
  const written=row.events.filter(x=>x.operation==='pipe-write').map(x=>x.hex.toLowerCase()).join('')
  const replies=row.events.filter(x=>x.operation==='reply'&&['@wire','@wire-terminal'].includes(x.arguments?.find(a=>a.parameter==='label')?.value.value)).map(x=>x.arguments.find(a=>a.parameter==='record').value.value)
  assert.equal(Buffer.from(replies.map(x=>x+'\n').join('')).toString('hex'),written)
  assert.equal(Buffer.from(row.outputs.map(x=>x+'\n').join('')).toString('hex'),written)
 }
 const gap=JSON.parse(await readFile(join(dir,'restart-proof.json'),'utf8'))
 assert.equal(hash(await readFile(join(dir,'restart-raw.json'))),gap.hashes.raw)
 assert.equal(hash(await readFile(resolve('examples/offline-session-reader-propagation/probe-restart.mjs'))),gap.hashes.source)
 assert.deepEqual(gap.repeatedEffects,{commit:2,head:2,save:2})
 const restarted=JSON.parse(await readFile(join(dir,'restart-raw.json'),'utf8'));assert.equal(restarted.length,2)
 for(const row of restarted){
  const offered=row.actions.filter(x=>x.writeHex).map(x=>x.writeHex).join('')
  const read=row.events.filter(x=>x.operation==='pipe-read').map(x=>x.hex.toLowerCase()).join('')
  assert.equal(read,offered,'both restart processes consumed the identical complete request bytes')
  const written=row.events.filter(x=>x.operation==='pipe-write').map(x=>x.hex.toLowerCase()).join('')
  assert.equal(Buffer.from(row.outputs.map(x=>x+'\n').join('')).toString('hex'),written)
  const replies=row.events.filter(x=>x.operation==='reply'&&['@wire','@wire-terminal'].includes(x.arguments?.find(a=>a.parameter==='label')?.value.value)).map(x=>x.arguments.find(a=>a.parameter==='record').value.value)
  const encoded=Buffer.from(replies.map(x=>x+'\n').join('')).toString('hex')
  assert.ok(encoded.startsWith(written));if(row.lostResponse)assert.ok(written.length<encoded.length);else assert.equal(written,encoded)
 }
 restarts.push(restarted)
 proofs.push(p);rows.push(raw)
}
const exact=Object.keys(proofs[0].hashes).filter(x=>x!=='pipe-raw.json')
for(const file of exact)assert.equal(proofs[0].hashes[file],proofs[1].hashes[file],file)
assert.deepEqual(proofs[0].proof,proofs[1].proof);assert.deepEqual(proofs[0].guards,proofs[1].guards);assert.deepEqual(proofs[0].sourceHashes,proofs[1].sourceHashes)
const stable=raw=>raw.map(r=>({id:r.id,deferred:r.deferred,control:r.control,scenario:r.scenario,actions:r.actions,outputs:r.outputs,outcome:r.events.find(x=>x.operation==='outcome'),exitCode:r.exitCode,signal:r.signal}))
assert.deepEqual(stable(rows[0]),stable(rows[1]))
const restartStable=raw=>raw.map(r=>({lostResponse:r.lostResponse,actions:r.actions,outputs:r.outputs,outcome:r.outcome,exitCode:r.exitCode,signal:r.signal}))
assert.deepEqual(restartStable(restarts[0]),restartStable(restarts[1]))
await writeFile(join(dirs[0],'verified.json'),JSON.stringify({movedReplay:true,exactFiles:exact,artifactHashClosure:true,sourceHashClosure:true,actualPipeRows:11,positiveRows:8,causalControls:2,guards:1,restartProcesses:2,restartRawHashesChecked:true,inputBytesChecked:true,acceptedWriteBytesChecked:true},null,2)+'\n')
console.log('PASS: moved replay, exact re-emission/recompilation, 11 pipe rows, byte and hash closure')
