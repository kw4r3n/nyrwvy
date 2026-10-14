import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {hash} from './git-fixture.mjs'
const dirs=process.argv.slice(2).map(x=>resolve(x));assert.equal(dirs.length,2)
const bundles=[]
function normalizedStorage(storage){return Object.fromEntries(Object.entries(storage).map(([key,value])=>{
 try{const p=JSON.parse(value);if(p.Repository){p.Repository='<fixture>';if(p.RepositoryId!=='unowned')p.RepositoryId='<fixture-id>';if(p.Before)p.Before.IndexBytes='<index-stat-bytes>';if(p.Invocation){p.Invocation='<child-invocation>';p.Pid='<child-pid>';}return [key,JSON.stringify(p)]}}catch{}return [key,value]
}))}
const physical=p=>({...p,index:'<index-stat-bytes>'})
for(const dir of dirs){
 const raw=JSON.parse(await readFile(join(dir,'raw.json'))),proof=JSON.parse(await readFile(join(dir,'proof.json')));assert.equal(proof.rawHash,hash(JSON.stringify(raw)));assert.equal(proof.processes,raw.length)
 bundles.push({proof,rawHash:hash(await readFile(join(dir,'raw.json'))),stable:raw.map(r=>({name:r.name,index:r.index,actions:r.actions,outputs:r.outputs,code:r.code,signal:r.signal,before:normalizedStorage(r.before),after:normalizedStorage(r.after),physicalBefore:physical(r.physicalBefore),physicalAfter:physical(r.physicalAfter)}))})
}
assert.deepEqual(bundles[0].proof.contracts,bundles[1].proof.contracts);assert.deepEqual(bundles[0].stable,bundles[1].stable)
await writeFile(join(dirs[0],'verified.json'),JSON.stringify({movedStableObservations:true,bundles:bundles.map(({stable,...rest})=>rest),normalization:['fixture path/id, index stat bytes and child PID/random invocation only in cross-domain comparison; local no-write/replay assertions use raw facts'],sourceHash:hash(await readFile(new URL(import.meta.url)))},null,2)+'\n')
console.log(`PASS: ${bundles.reduce((n,b)=>n+b.proof.processes,0)} child processes with original/moved stable facts`)
