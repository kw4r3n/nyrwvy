import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {createHash} from 'node:crypto'
const dirs=process.argv.slice(2).map(x=>resolve(x)),hash=x=>createHash('sha256').update(x).digest('hex')
assert.equal(dirs.length,2,'original and moved bundle required')
const proofs=[],allRows=[]
for(const dir of dirs){
 const proof=JSON.parse(await readFile(join(dir,'pipe-proof.json'),'utf8'))
 for(const [file,expected] of Object.entries(proof.hashes))assert.equal(hash(await readFile(join(dir,file))),expected,file)
 for(const [file,expected] of Object.entries(proof.sourceHashes))assert.equal(hash(await readFile(resolve(file))),expected,file)
 const rows=JSON.parse(await readFile(join(dir,'pipe-raw.json'),'utf8'))
 assert.equal(rows.length,24);assert.equal(new Set(rows.map(x=>x.id+'/'+x.control)).size,24)
 assert.equal(proof.proof.filter(x=>!x.rejected).length,20);assert.equal(proof.proof.filter(x=>x.rejected).length,3)
 assert.deepEqual(proof.guards,[{id:'unrelated-input-fault-guard',refusedAsOversize:true,type:'NotSupportedException'}])
 for(const row of rows){
  const offered=row.actions.filter(x=>x.writeHex||x.writeQueuedHex).map(x=>x.writeHex??x.writeQueuedHex).join('')
  const read=row.events.filter(x=>x.operation==='pipe-read').map(x=>x.hex.toLowerCase()).join('')
  assert.ok(offered.startsWith(read),`actual input is exact offered prefix: ${row.id}/${row.control}`)
  if(row.id==='one-mib-unterminated'&&!row.control){assert.ok(read.length/2>=16385&&read.length/2<=16384+1024);assert.ok(read.length<offered.length,'1 MiB probe terminates without draining oversized frame')}
  if(row.control==='relaxed-policy'&&!row.scenario.terminator)assert.equal(read,offered,'relaxed graph actually reads every supplied byte')
  const written=row.events.filter(x=>x.operation==='pipe-write').map(x=>x.hex.toLowerCase()).join('')
  const replies=row.events.filter(x=>x.operation==='reply'&&['@wire','@wire-terminal'].includes(x.arguments?.find(a=>a.parameter==='label')?.value.value)).map(x=>x.arguments.find(a=>a.parameter==='record').value.value)
  const encoded=Buffer.from(replies.map(x=>x+'\n').join('')).toString('hex')
  assert.equal(encoded,written,'complete accepted writes equal graph replies')
  assert.equal(Buffer.from(row.outputs.map(x=>x+'\n').join('')).toString('hex'),written,'peer output equals accepted OS bytes')
 }
 proofs.push(proof);allRows.push(rows)
}
const exact=Object.keys(proofs[0].hashes).filter(x=>x!=='pipe-raw.json')
for(const file of exact)assert.equal(proofs[0].hashes[file],proofs[1].hashes[file],`moved exact ${file}`)
assert.deepEqual(proofs[0].sourceHashes,proofs[1].sourceHashes)
assert.deepEqual(proofs[0].proof,proofs[1].proof)
assert.deepEqual(proofs[0].guards,proofs[1].guards)
const stable=rows=>rows.map(row=>({id:row.id,control:row.control,scenario:row.scenario,actions:row.actions,outputs:row.outputs,outcome:row.events.find(x=>x.operation==='outcome'),exitCode:row.exitCode,signal:row.signal}))
assert.deepEqual(stable(allRows[0]),stable(allRows[1]),'same exact supplied bytes, peer actions, outputs and outcomes; read fragments, interleavings and timing may differ')
await writeFile(join(dirs[0],'verified.json'),JSON.stringify({movedReplay:true,exactFiles:exact,artifactHashClosure:true,sourceHashClosure:true,actualPipeRows:24,positiveRows:20,causalControls:3,verificationGuards:1,inputBytesChecked:true,acceptedWriteBytesChecked:true},null,2)+'\n')
console.log('PASS: moved snapshot reopen, exact source/host/assembly bytes, 24 pipe rows and source/artifact hash closure')
