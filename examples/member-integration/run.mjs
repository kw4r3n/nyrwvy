import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author} from './author.mjs'
import {scenarios,argumentsFor,validate,observe} from './cases.mjs'
const baseline=process.argv[2]==='--baseline',replay=baseline||process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]),hash=x=>createHash('sha256').update(x).digest('hex')
await mkdir(dir,{recursive:true});if(!replay)assert.deepEqual(await readdir(dir),[])
const s=await session('integration',dir)
try{
 if(!replay){const {artifact,nodes}=await author(s);await writeFile(join(dir,'Generated.cs'),artifact.source);await writeFile(join(dir,'symbols.json'),JSON.stringify(artifact.symbols,null,2));await s.call('close');const graph=JSON.parse(await readFile(s.snapshot,'utf8'));await writeFile(join(dir,'cases.json'),JSON.stringify(scenarios().map(c=>({...c,arguments:argumentsFor(c,graph)})),null,2));await writeFile(join(dir,'authoring.json'),JSON.stringify({base:'37e512c682b32bbacdf40e9b47f26910f2b59c68',oracle:'4bab9e030744a0887c1ee13bdd8fc8db3b98334f',nodes,functionCount:Object.keys(nodes).length,nodeCount:Object.values(nodes).reduce((a,b)=>a+b,0),generatedSha256:hash(artifact.source),nodeVersion:process.version},null,2))}
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const metadata=JSON.parse(await readFile(join(dir,'authoring.json'),'utf8')),declarations=await s.call('inspect',{view:'declarations'})
 const regenerated=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:[]}}})
 assert.equal(hash(regenerated.source),metadata.generatedSha256);assert.deepEqual(regenerated.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 const snapshotSha256=hash(await readFile(s.snapshot)),evidence=[]
 for(const c of JSON.parse(await readFile(join(dir,'cases.json'),'utf8'))){
  const requests=[],responses=[]
  // Fresh deterministic prefix runs expose each fold request without adding an observer to the graph.
  for(let request=1;request<=c.requests;request++){
   const args=structuredClone(c.arguments);args[2].items=args[2].items.slice(0,request)
   const response=await s.exchange('request',{op:'runPureByName',arguments:{function:'run',arguments:args}})
   requests.push(baseline?observe(c,response):validate(c,response,request));responses.push(response)
  }
  evidence.push({checks:{...requests.at(-1),requests},responses});console.log(`${baseline?'OBSERVE baseline':'PASS'}: integration ${c.id} (${requests.length} requests)`)
 }
 assert.equal(hash(await readFile(s.snapshot)),snapshotSha256)
 await writeFile(join(dir,replay?'replay-evidence.json':'evidence.json'),JSON.stringify(evidence,null,2))
 await writeFile(join(dir,replay?'replay-identity.json':'bundle-identity.json'),JSON.stringify({snapshotSha256,generatedSha256:metadata.generatedSha256,reemissionMatches:true,symbolsSha256:hash(await readFile(join(dir,'symbols.json'))),casesSha256:hash(await readFile(join(dir,'cases.json'))),status:await s.call('inspect',{view:'status'})},null,2))
}finally{await s.exchange('close')}
