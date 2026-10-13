import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {createHash} from 'node:crypto'
import assert from 'node:assert/strict'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author} from './author.mjs'
import {scenarios,argumentsFor,acceptance,normalize} from './cases.mjs'
const replay=process.argv[2]==='--replay',baseline=process.argv.includes('--baseline'),dir=resolve(process.argv[replay?3:2]??'member-evidence')
await mkdir(dir,{recursive:true})
if(!replay)assert.deepEqual(await readdir(dir),[],'use an empty evidence directory')
const hash=x=>createHash('sha256').update(x).digest('hex')
const s=await session('controller',dir)
try {
 if(!replay){
  // Both variants use the same token audits and cancellation harness.
  const {artifact,nodes}=await author(s,{containSnapshotFaults:!baseline})
  await writeFile(join(dir,'Generated.cs'),artifact.source)
  await writeFile(join(dir,'symbols.json'),JSON.stringify(artifact.symbols,null,2))
  await writeFile(join(dir,'authoring.json'),JSON.stringify({base:'9e56e835e0978d539f41778f8e2149c8c4b2b279',oracle:'4bab9e030744a0887c1ee13bdd8fc8db3b98334f',baseline,nodes,functionCount:Object.keys(nodes).length,nodeCount:Object.values(nodes).reduce((a,b)=>a+b,0),generatedSha256:hash(artifact.source),nodeVersion:process.version},null,2))
  await writeFile(join(dir,'cases.json'),JSON.stringify(scenarios().map(c=>({...c,arguments:argumentsFor(c)})),null,2))
  await s.call('close')
 }
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const original=JSON.parse(await readFile(join(dir,'authoring.json'),'utf8'))
 const declarations=await s.call('inspect',{view:'declarations'})
 const reemitted=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 assert.equal(hash(reemitted.source),original.generatedSha256,'reopened projection must match saved projection')
 assert.deepEqual(reemitted.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')),'reopened symbol identities')
 const snapshotBefore=hash(await readFile(s.snapshot))
 const evidence=[]
 for(const c of JSON.parse(await readFile(join(dir,'cases.json'),'utf8'))){
  const response=await s.exchange('request',{op:'runPureByName',arguments:{function:c.function??'ownerProbe',arguments:c.arguments}})
  const checks=normalize(c,response);if(!baseline&&!original.baseline)acceptance(c,checks);evidence.push({checks,response});console.log(`PASS: ${c.id}`)
 }
 await writeFile(join(dir,replay?'replay-evidence.json':'evidence.json'),JSON.stringify(evidence,null,2))
 assert.equal(hash(await readFile(s.snapshot)),snapshotBefore,'execution never edits the graph')
 const identity={snapshotSha256:snapshotBefore,generatedSha256:original.generatedSha256,reemissionMatches:true,symbolsSha256:hash(await readFile(join(dir,'symbols.json'))),casesSha256:hash(await readFile(join(dir,'cases.json'))),status:await s.call('inspect',{view:'status'})}
 await writeFile(join(dir,replay?'replay-identity.json':'bundle-identity.json'),JSON.stringify(identity,null,2))
 console.log(`PASS: saved/reopened ${evidence.length} virtual cases; ${dir}`)
}finally{await s.exchange('close')}
