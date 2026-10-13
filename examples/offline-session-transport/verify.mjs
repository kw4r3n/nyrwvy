import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {createHash} from 'node:crypto'
const dirs=process.argv.slice(2).map(x=>resolve(x)),hash=x=>createHash('sha256').update(x).digest('hex')
assert.equal(dirs.length,2,'original and moved bundle required')
const proofs=[]
for(const dir of dirs){
 const proof=JSON.parse(await readFile(join(dir,'pipe-proof.json'),'utf8'))
 for(const [file,expected] of Object.entries(proof.hashes))assert.equal(hash(await readFile(join(dir,file))),expected,file)
 for(const [file,expected] of Object.entries(proof.sourceHashes))assert.equal(hash(await readFile(resolve(file))),expected,file)
 const rows=JSON.parse(await readFile(join(dir,'pipe-raw.json'),'utf8'))
 assert.equal(rows.length,18);assert.equal(proof.proof.filter(x=>x.rejected).length,4)
 assert.deepEqual(proof.verificationGuards,[{id:'unrelated-tail-control-fault',refusedAsTailLoss:true,contract:'application-success',observedType:'NotSupportedException'}])
 for(const row of rows){
  const sent=row.actions.filter(x=>x.writeHex).map(x=>x.writeHex).join('')
  const read=row.events.filter(x=>x.operation==='pipe-read').map(x=>x.hex.toLowerCase()).join('')
  assert.equal(read,sent,`actual read bytes equal sent bytes: ${row.id}/${row.control}`)
 }
 proofs.push(proof)
}
const exact=Object.keys(proofs[0].hashes).filter(x=>x!=='pipe-raw.json')
for(const file of exact)assert.equal(proofs[0].hashes[file],proofs[1].hashes[file],`moved exact ${file}`)
assert.deepEqual(proofs[0].sourceHashes,proofs[1].sourceHashes)
assert.deepEqual(proofs[0].proof,proofs[1].proof,'same positive and negative contracts')
assert.deepEqual(proofs[0].verificationGuards,proofs[1].verificationGuards,'same refusal of unrelated negative-control faults')
const rows=await Promise.all(dirs.map(async dir=>JSON.parse(await readFile(join(dir,'pipe-raw.json'),'utf8'))))
const stable=rows=>rows.map(row=>({id:row.id,control:row.control,scenario:row.scenario,actions:row.actions,outputs:row.outputs,outcome:row.events.find(x=>x.operation==='outcome'),exitCode:row.exitCode,signal:row.signal}))
assert.deepEqual(stable(rows[0]),stable(rows[1]),'same exact wire bytes, scripted peer actions and outcomes; independent effect interleavings may differ')
await writeFile(join(dirs[0],'verified.json'),JSON.stringify({movedReplay:true,exactFiles:exact,artifactHashClosure:true,sourceHashClosure:true,actualPipeRows:18,negativeControls:4,verificationGuards:1,readBytesMatchSent:true},null,2)+'\n')
console.log('PASS: moved snapshot reopen, exact source/host/assembly bytes, pipe outputs/outcomes, artifact/source hash closure')
