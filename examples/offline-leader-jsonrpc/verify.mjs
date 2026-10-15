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
for(const [file,key] of [['symbols.json','symbolsSha256'],['cases.json','casesSha256'],['parser-cases.json','parserCasesSha256'],['declarations.json','declarationsSha256']])assert.equal(hash(await readFile(join(dir,file))),identity[key])
assert.equal(identity.reemissionMatches,true)
assert.equal(virtual.length,1);assert.equal(runtime.proof.length,13)
const controls=runtime.proof.filter(x=>x.id==='compiled-command-boundary')
assert.deepEqual(controls.map(x=>({wrongBoundary:x.wrongBoundary,rejected:x.rejected})),[{wrongBoundary:false,rejected:false},{wrongBoundary:true,rejected:true}])
for(const name of ['compiled-wire-parser','compiled-wire-validation'])assert.deepEqual(runtime.proof.filter(x=>x.id===name).map(x=>x.rejected),[false,true])
if(moved){
 for(const name of ['session.json','Generated.cs','symbols.json','cases.json','parser-cases.json','declarations.json','Runtime.dll','Program.cs','runtime-cases.json'])assert.deepEqual(await readFile(join(dir,name)),await readFile(join(moved,name)),name)
 assert.deepEqual(await json(moved,'replay-evidence.json'),virtual)
 const replay=await json(moved,'replay-identity.json')
 for(const key of ['snapshotSha256','generatedSha256','symbolsSha256','casesSha256','parserCasesSha256','declarationsSha256'])assert.equal(replay[key],identity[key])
 assert.deepEqual(await json(moved,'runtime-evidence.json'),runtime)
}
const files=['session.json','Generated.cs','symbols.json','declarations.json','authoring.json','cases.json','parser-cases.json','evidence.json','identity.json','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','runtime-cases.json','runtime-raw.jsonl','runtime-evidence.json','transcript.jsonl']
const closure=Object.fromEntries(await Promise.all(files.map(async name=>[name,hash(await readFile(join(dir,name)))])))
const sources=['author.mjs','parser.mjs','recipes.mjs','cases.mjs','run.mjs','test-runtime.mjs','verify.mjs','runtime.template.cs']
const sourceClosure=Object.fromEntries(await Promise.all(sources.map(async name=>[name,hash(await readFile(new URL(name,import.meta.url)))])))
sourceClosure['../offline-leader-team/author.mjs']=hash(await readFile(new URL('../offline-leader-team/author.mjs',import.meta.url)))
const proof={claim:'bounded offline JSON-RPC wire → Leader → Team → two Members session',base:'e2fc3a0488171d9978a781f519c7a0dca20e1206',dependsOn:'PR395',historicalProbe9Recovered:false,ordinaryAuthoring:true,savedReopened:true,movedReplay:!!moved,uneditedEmission:true,offlineCompilation:runtime.compilation.method,compiledAssemblySha256:runtime.assemblySha256,identity,parserCases:virtual[0].count,compiledControls:runtime.proof.length,controls:runtime.proof,closure,sourceClosure}
await writeFile(join(dir,'proof.json'),JSON.stringify(proof,null,2)+'\n')
console.log(`PASS: durable proof${moved?' and byte-identical moved replay':''}; ${join(dir,'proof.json')}`)
