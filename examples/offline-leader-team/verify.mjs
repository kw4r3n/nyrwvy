import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
const dir=resolve(process.argv[2]),moved=process.argv[3]?resolve(process.argv[3]):undefined
const json=async(root,name)=>JSON.parse(await readFile(join(root,name),'utf8')),hash=x=>createHash('sha256').update(x).digest('hex')
const identity=await json(dir,'identity.json'),runtime=await json(dir,'runtime-evidence.json'),virtual=await json(dir,'evidence.json')
assert.equal(runtime.sourceUnchanged,true)
assert.equal(runtime.generatedSha256,identity.generatedSha256)
assert.equal(hash(await readFile(join(dir,'session.json'))),identity.snapshotSha256)
assert.equal(hash(await readFile(join(dir,'Generated.cs'))),identity.generatedSha256)
assert.equal(hash(await readFile(join(dir,'Runtime.dll'))),runtime.assemblySha256)
assert.equal(identity.reemissionMatches,true)
assert.equal(virtual.length,1);assert.equal(runtime.proof.length,9)
const controls=runtime.proof.filter(x=>x.id==='compiled-command-boundary')
assert.deepEqual(controls.map(x=>({wrongBoundary:x.wrongBoundary,rejected:x.rejected})),[{wrongBoundary:false,rejected:false},{wrongBoundary:true,rejected:true}])
if(moved){
 for(const name of ['session.json','Generated.cs','symbols.json','cases.json','declarations.json','Runtime.dll'])assert.deepEqual(await readFile(join(dir,name)),await readFile(join(moved,name)),name)
 assert.deepEqual(await json(moved,'replay-evidence.json'),virtual)
 const replay=await json(moved,'replay-identity.json')
 for(const key of ['snapshotSha256','generatedSha256','symbolsSha256','casesSha256'])assert.equal(replay[key],identity[key])
 assert.deepEqual(await json(moved,'runtime-evidence.json'),runtime)
}
const proof={claim:'bounded offline scripted Leader → Team → Member session',base:'7f4dc33a33db3c9079359b04a851dc7010f0d784',ordinaryAuthoring:true,savedReopened:true,movedReplay:!!moved,uneditedEmission:true,offlineCompilation:runtime.compilation.method,compiledAssemblySha256:runtime.assemblySha256,identity,virtualSessions:virtual.length,compiledControls:runtime.proof.length,controls:runtime.proof}
await writeFile(join(dir,'proof.json'),JSON.stringify(proof,null,2)+'\n')
console.log(`PASS: durable proof${moved?' and byte-identical moved replay':''}; ${join(dir,'proof.json')}`)
