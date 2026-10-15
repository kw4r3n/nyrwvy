import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {createHash} from 'node:crypto'
const dirs=process.argv.slice(2).map(x=>resolve(x)),hash=x=>createHash('sha256').update(x).digest('hex')
assert.equal(dirs.length,2)
async function normalizedHost(dir){
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
const bundles=[],sourceHashes={},stable=[],repairs=[],repairBundles=[]
for(const dir of dirs){
 assert.equal(await normalizedHost(dir),await normalizedHost(join(dir,'restoration-disabled')),'identical supported/control binding after declared-symbol normalization')
 const hashes={};for(const prefix of ['','restoration-disabled/'])for(const file of files)hashes[prefix+file]=hash(await readFile(join(dir,prefix+file)))
 const proof=JSON.parse(await readFile(join(dir,'registry-proof.json'),'utf8')),rows=JSON.parse(await readFile(join(dir,'registry-raw.json'),'utf8'))
 assert.equal(proof.hashes.raw,hash(await readFile(join(dir,'registry-raw.json'))));assert.equal(proof.hashes.source,hash(await readFile(new URL('./test-runtime.mjs',import.meta.url))))
 assert.equal(rows.length,proof.rows)
 for(const row of rows){
  const offered=row.actions.filter(x=>x.writeHex).map(x=>x.writeHex).join(''),read=row.events.filter(x=>x.operation==='pipe-read').map(x=>x.hex.toLowerCase()).join('')
  assert.ok(offered.startsWith(read),row.name);if(!row.fault)assert.equal(read,offered,row.name)
  const written=row.events.filter(x=>x.operation==='pipe-write').map(x=>x.hex.toLowerCase()).join('')
  assert.equal(Buffer.from(row.outputs.map(x=>x+'\n').join('')).toString('hex'),written,row.name)
  const replies=row.events.filter(x=>x.operation==='reply'&&['@wire','@wire-terminal'].includes(x.arguments.find(a=>a.parameter==='label')?.value.value)).map(x=>x.arguments.find(a=>a.parameter==='record').value.value)
  assert.equal(Buffer.from(replies.map(x=>x+'\n').join('')).toString('hex'),written,row.name)
 }
 // The causal runs share the complete scripted prefix and integration bytes.
 for(const index of [1,2,3]){
  const enabled=rows.find(r=>r.name==='restoration'&&r.index===index),disabled=rows.find(r=>r.name==='restoration-disabled'&&r.index===index)
  assert.deepEqual(enabled.actions,disabled.actions,'identical causal input/actions')
 }
 bundles.push({dir,hashes,rawHash:proof.hashes.raw,proof:proof.proof})
 stable.push(rows.map(r=>({name:r.name,index:r.index,restorationDisabled:r.restorationDisabled,crash:r.crash,fault:r.fault,actions:r.actions,outputs:r.outputs,exitCode:r.exitCode,signal:r.signal,storageBefore:r.storageBefore,storageAfter:r.storageAfter})))
 const repaired=JSON.parse(await readFile(join(dir,'repair/proof.json'),'utf8')),repairRows=JSON.parse(await readFile(join(dir,'repair/raw.json'),'utf8'))
 assert.equal(repaired.rawSha256,hash(await readFile(join(dir,'repair/raw.json'))));assert.equal(repaired.sourceSha256,hash(await readFile(new URL('./test-repair.mjs',import.meta.url))))
 assert.equal(repaired.seedRawSha256,proof.hashes.raw);assert.equal(repaired.seedInputKind,'fresh baseline-matrix inputs from this repaired bundle')
 assert.equal(repaired.processes,repairRows.length)
 for(const [file,value] of Object.entries(repaired.runtimeManifest))assert.equal(value,hashes[file])
 repairs.push({proof:repaired.proof,rows:repairRows.map(r=>({name:r.name,actions:r.actions,outputs:r.outputs,exitCode:r.exitCode,signal:r.signal,storageBefore:r.storageBefore,storageAfter:r.storageAfter}))})
 repairBundles.push({dir:join(dir,'repair'),processes:repaired.processes,contracts:repaired.proof.length,rawSha256:repaired.rawSha256,sourceSha256:repaired.sourceSha256,seedRawSha256:repaired.seedRawSha256,runtimeManifest:repaired.runtimeManifest})
}
assert.deepEqual(bundles[0].hashes,bundles[1].hashes,'moved artifact bytes');assert.deepEqual(bundles[0].proof,bundles[1].proof);assert.deepEqual(stable[0],stable[1],'fresh moved observations')
assert.deepEqual(repairs[0],repairs[1],'fresh moved repair controls')
async function source(path){
 const relative=path.slice(resolve('.').length+1);if(sourceHashes[relative])return
 const bytes=await readFile(path);sourceHashes[relative]=hash(bytes)
 for(const m of bytes.toString().matchAll(/(?:from\s+|import\s*)['"]([^'"]+)['"]/g))if(m[1].startsWith('.'))await source(resolve(path,'..',m[1]))
}
for(const file of ['author.mjs','run.mjs','compile.mjs','test-runtime.mjs','test-repair.mjs','verify.mjs'])await source(resolve('examples/offline-session-registry',file))
for(const file of ['examples/offline-session-journal/storage.template.cs','examples/offline-session-owned-io/pipe.template.cs','examples/offline-session-bounded-input/pipe.template.cs','examples/offline-leader-jsonrpc/runtime.template.cs','.opencode/tests/nyrwvy-authoring-offline-session-registry.test.mjs'])await source(resolve(file))
await writeFile(join(dirs[0],'verified.json'),JSON.stringify({movedReplay:true,identicalControlBinding:true,normalizedHostHash:hash(await normalizedHost(dirs[0])),inputBytesChecked:true,acceptedWriteBytesChecked:true,artifactHashClosure:true,sourceHashes,bundles,repairBundles,limits:['example-source/artifact closure excludes core/tool implementation, compiler, references, environment and power-loss behavior; core/tool sources remain pinned by base commit','fresh processes and SIGKILL establish selected single-writer windows, not arbitrary interruptions or physical/provider safety','the final repair execution reuses the reviewed disabled-control artifact and compilation; its processes are fresh']},null,2)+'\n')
console.log(`PASS: moved exact emission/compilation; ${files.length*2} artifacts; ${bundles[0].proof.length} baseline and ${repairs[0].proof.length} repair contracts; raw, source and actual byte checks`)
