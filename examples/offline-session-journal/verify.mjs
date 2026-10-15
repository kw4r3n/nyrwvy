import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {createHash} from 'node:crypto'
const dirs=process.argv.slice(2).map(x=>resolve(x)),hash=x=>createHash('sha256').update(x).digest('hex')
assert.equal(dirs.length,2)
export async function normalizedHost(dir){
 const d=JSON.parse(await readFile(join(dir,'declarations.json'),'utf8')),s=JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')),labels=new Map()
 function walk(v,path=''){
  if(Array.isArray(v)){for(const x of v)walk(x,path);return}if(!v||typeof v!=='object')return
  if(v.displayName){path+='/'+v.displayName;if(v.id)labels.set(v.id,path)}
  for(const [key,x] of Object.entries(v))if(x&&typeof x==='object')walk(x,path+'/'+key)
 }
 walk(d);let source=await readFile(join(dir,'Program.cs'),'utf8')
 for(const symbol of Object.values(s).filter(Array.isArray).flat().sort((a,b)=>b.name.length-a.name.length))if(source.includes(symbol.name)){assert.ok(labels.has(symbol.id));source=source.split(symbol.name).join('@@'+labels.get(symbol.id)+'@@')}
 return source
}
const files=['Generated.cs','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','session.json','symbols.json','declarations.json','authoring.json','compilation.json']
const bundles=[],sourceHashes={},stable=[]
for(const dir of dirs){
 assert.equal(await normalizedHost(dir),await normalizedHost(join(dir,'disabled')),'identical supported/control binding after declared-symbol normalization')
 assert.equal(await normalizedHost(dir),await normalizedHost(join(dir,'receipt-disabled')),'identical receipt-enabled/control binding')
 const hashes={};for(const prefix of ['','disabled/','receipt-disabled/'])for(const file of files)hashes[prefix+file]=hash(await readFile(join(dir,prefix+file)))
 const proof=JSON.parse(await readFile(join(dir,'journal-proof.json'),'utf8')),rows=JSON.parse(await readFile(join(dir,'journal-raw.json'),'utf8'))
 assert.equal(proof.hashes.raw,hash(await readFile(join(dir,'journal-raw.json'))));assert.equal(proof.hashes.source,hash(await readFile(new URL('./test-runtime.mjs',import.meta.url))))
 assert.equal(rows.length,proof.rows)
 for(const row of rows){
  const offered=row.actions.filter(x=>x.writeHex).map(x=>x.writeHex).join(''),read=row.events.filter(x=>x.operation==='pipe-read').map(x=>x.hex.toLowerCase()).join('')
  assert.ok(offered.startsWith(read),row.name)
  const written=row.events.filter(x=>x.operation==='pipe-write').map(x=>x.hex.toLowerCase()).join('')
  assert.equal(Buffer.from(row.outputs.map(x=>x+'\n').join('')).toString('hex'),written,row.name)
  // Fault probes deliberately end mid-frame; every other process consumes all
  // complete requests, including processes killed at explicit checkpoints.
  if(!row.fault)assert.equal(read,offered,row.name)
  const replies=row.events.filter(x=>x.operation==='reply'&&['@wire','@wire-terminal'].includes(x.arguments.find(a=>a.parameter==='label')?.value.value)).map(x=>x.arguments.find(a=>a.parameter==='record').value.value)
  const encoded=Buffer.from(replies.map(x=>x+'\n').join('')).toString('hex')
  assert.ok(encoded.startsWith(written));if(!row.lost&&!row.crash)assert.equal(encoded,written)
 }
 bundles.push({dir,hashes,rawHash:proof.hashes.raw,proof:proof.proof})
 stable.push(rows.map(r=>({name:r.name,index:r.index,disabled:r.disabled,receiptDisabled:r.receiptDisabled,lost:r.lost,crash:r.crash,fault:r.fault,actions:r.actions,outputs:r.outputs,exitCode:r.exitCode,signal:r.signal,storageBefore:r.storageBefore,storageAfter:r.storageAfter})))
}
assert.deepEqual(bundles[0].hashes,bundles[1].hashes,'moved artifact bytes');assert.deepEqual(bundles[0].proof,bundles[1].proof);assert.deepEqual(stable[0],stable[1],'fresh moved observations')
async function source(path){
 const relative=path.slice(resolve('.').length+1);if(sourceHashes[relative])return
 const bytes=await readFile(path);sourceHashes[relative]=hash(bytes)
 for(const m of bytes.toString().matchAll(/(?:from\s+|import\s*)['"]([^'"]+)['"]/g))if(m[1].startsWith('.'))await source(resolve(path,'..',m[1]))
}
for(const file of ['author.mjs','run.mjs','compile.mjs','storage.template.cs','test-runtime.mjs','verify.mjs'])await source(resolve('examples/offline-session-journal',file))
for(const file of ['examples/offline-session-owned-io/pipe.template.cs','examples/offline-session-bounded-input/pipe.template.cs','examples/offline-leader-jsonrpc/runtime.template.cs','.opencode/tests/nyrwvy-authoring-offline-session-journal.test.mjs','examples/offline-session-journal/fixtures/legacy-outcome-state.json'])await source(resolve(file))
await writeFile(join(dirs[0],'verified.json'),JSON.stringify({movedReplay:true,identicalControlBinding:true,normalizedHostHash:hash(await normalizedHost(dirs[0])),inputBytesChecked:true,acceptedWriteBytesChecked:true,artifactHashClosure:true,sourceHashes,bundles,limits:['example-source/artifact closure excludes core/tool implementation, compiler, references, environment and host filesystem durability under power loss; core/tool sources remain pinned by base commit','fresh processes and SIGKILL establish the observed windows, not arbitrary interruption or multi-writer safety']},null,2)+'\n')
console.log(`PASS: moved exact emission/compilation; ${files.length*3} artifacts; ${bundles[0].proof.length} contracts; raw, source and actual byte checks`)
