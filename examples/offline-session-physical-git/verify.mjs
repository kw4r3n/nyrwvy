import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {hash} from './git-fixture.mjs'
const dirs=process.argv.slice(2).map(x=>resolve(x));assert.equal(dirs.length,2)
async function normalizedHost(dir){
 const d=JSON.parse(await readFile(join(dir,'declarations.json'),'utf8')),s=JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')),labels=new Map()
 function walk(v,path=''){if(Array.isArray(v)){for(const x of v)walk(x,path);return}if(!v||typeof v!=='object')return;if(v.displayName){path+='/'+v.displayName;if(v.id)labels.set(v.id,path)}for(const [key,x] of Object.entries(v))if(x&&typeof x==='object')walk(x,path+'/'+key)}
 walk(d);let source=await readFile(join(dir,'Program.cs'),'utf8')
 for(const symbol of Object.values(s).filter(Array.isArray).flat().sort((a,b)=>b.name.length-a.name.length))if(source.includes(symbol.name)){assert.ok(labels.has(symbol.id));source=source.split(symbol.name).join('@@'+labels.get(symbol.id)+'@@')}
 return source
}
const files=['session.json','Generated.cs','symbols.json','declarations.json','authoring.json','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','compilation.json'],bundles=[]
const stable=[]
function normalizedStorage(state){return Object.fromEntries(Object.entries(state).map(([key,value])=>{
 try{const p=JSON.parse(value);if(p.Repository){p.Repository='<fixture>';if(p.RepositoryId!=='unowned')p.RepositoryId='<fixture-id>';if(p.Before)p.Before.IndexBytes='<index-stat-bytes>';if(p.Invocation){p.Invocation='<child-invocation>';p.Pid='<child-pid>';}return [key,JSON.stringify(p)]}}catch{}return [key,value]
}))}
const normalizedState=p=>({...p,index:'<index-stat-bytes>'})
for(const dir of dirs){
 assert.equal(await normalizedHost(dir),await normalizedHost(join(dir,'restoration-disabled')),'paired graphs share the normalized physical binding')
 const proof=JSON.parse(await readFile(join(dir,'physical-proof.json'),'utf8')),rows=JSON.parse(await readFile(join(dir,'physical-raw.json'),'utf8'))
 assert.equal(rows.length,proof.rows);assert.equal(proof.hashes.raw,hash(await readFile(join(dir,'physical-raw.json'))));assert.equal(proof.hashes.source,hash(await readFile(new URL('./test-runtime.mjs',import.meta.url))))
 const artifacts={};for(const prefix of ['','restoration-disabled/'])for(const file of files)artifacts[prefix+file]=hash(await readFile(join(dir,prefix+file)))
 for(const row of rows){
  const offered=row.actions.filter(a=>a.writeHex).map(a=>a.writeHex).join(''),read=row.events.filter(e=>e.operation==='pipe-read').map(e=>e.hex.toLowerCase()).join('')
  assert.ok(offered.startsWith(read));if(!row.fault)assert.equal(offered,read)
  const accepted=row.events.filter(e=>e.operation==='pipe-write').map(e=>e.hex.toLowerCase()).join('')
  assert.equal(accepted,Buffer.from(row.outputs.map(x=>x+'\n').join('')).toString('hex'))
  assert.ok(row.events.filter(e=>e.operation==='physical-git-exit').every(e=>e.childExited&&e.exitCode===0))
  assert.ok(row.events.filter(e=>e.operation==='physical-git-exit'&&e.mutation).every(e=>['commit-tree','update-ref','read-tree'].includes(e.command)))
 }
 stable.push(rows.map(r=>({name:r.name,index:r.index,crash:r.crash,fault:r.fault,lost:r.lost,physicalControl:r.physicalControl,restorationDisabled:r.restorationDisabled,actions:r.actions,outputs:r.outputs,exitCode:r.exitCode,signal:r.signal,storageBefore:normalizedStorage(r.storageBefore),storageAfter:normalizedStorage(r.storageAfter),physicalBefore:normalizedState(r.physicalBefore),physicalAfter:normalizedState(r.physicalAfter)})))
 bundles.push({dir,artifacts,proof,rawSha256:proof.hashes.raw})
}
assert.deepEqual(bundles[0].artifacts,bundles[1].artifacts,'moved artifact bytes')
assert.deepEqual(bundles[0].proof.proof,bundles[1].proof.proof,'moved contract observations')
assert.deepEqual(stable[0],stable[1],'moved fresh observations, normalized only fixture paths/identity/index stat bytes')
// Same commit/checkpoint/input on the paired effect-evidence-rule control.
const raw=JSON.parse(await readFile(join(dirs[0],'physical-raw.json'),'utf8')),enabled=raw.find(r=>r.name==='after-git-object'&&r.index===1),control=raw.find(r=>r.name==='receipt-only-control'&&r.index===1)
assert.deepEqual(enabled.actions,control.actions);assert.deepEqual(enabled.physicalAfter.objects,control.physicalAfter.objects)
const sourceHashes={}
async function source(path){const rel=path.slice(resolve('.').length+1);if(sourceHashes[rel])return;const bytes=await readFile(path);sourceHashes[rel]=hash(bytes);for(const m of bytes.toString().matchAll(/(?:from\s+|import\s*)['"]([^'"]+)['"]/g))if(m[1].startsWith('.'))await source(resolve(path,'..',m[1]))}
for(const file of ['run.mjs','compile.mjs','probe.mjs','git-fixture.mjs','test-runtime.mjs','test-child-lifetime.mjs','verify-child-lifetime.mjs','verify.mjs','git.template.cs'])await source(resolve('examples/offline-session-physical-git',file))
for(const file of ['examples/offline-session-journal/storage.template.cs','examples/offline-session-owned-io/pipe.template.cs','examples/offline-session-bounded-input/pipe.template.cs','examples/offline-leader-jsonrpc/runtime.template.cs','.opencode/tests-extended/nyrwvy-authoring-offline-session-physical-git.test.mjs'])await source(resolve(file))
await writeFile(join(dirs[0],'physical-verified.json'),JSON.stringify({movedReplay:true,exactArtifactBytes:true,normalizedHostSha256:hash(await normalizedHost(dirs[0])),inputAndAcceptedWriteBytesChecked:true,sourceHashes,bundles,limits:['fixture path/id and per-run index stat-byte hash are normalized only for cross-domain comparison; each absence/restart assertion checks raw bytes locally','example-source/artifact closure excludes installed compiler/reference/runtime/environment closure','single trusted writer, clean provisioned standalone fixtures and subprocess exit checkpoints; no arbitrary interruption/power-loss/multi-writer proof']},null,2)+'\n')
console.log(`PASS: ${bundles[0].proof.rows*2} fresh physical processes, moved exact emission/compilation, normalized control binding and byte checks`)
