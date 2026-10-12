import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author} from './author.mjs'
import {scenarios,argumentsFor,validate} from './cases.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]),hash=x=>createHash('sha256').update(x).digest('hex')
await mkdir(dir,{recursive:true});if(!replay)assert.deepEqual(await readdir(dir),[])
const s=await session('followup',dir)
try{
 if(!replay){const {artifact,nodes}=await author(s);await writeFile(join(dir,'Generated.cs'),artifact.source);await writeFile(join(dir,'symbols.json'),JSON.stringify(artifact.symbols,null,2));await writeFile(join(dir,'cases.json'),JSON.stringify(scenarios().map(c=>({...c,arguments:argumentsFor(c)})),null,2));await writeFile(join(dir,'authoring.json'),JSON.stringify({base:'ec6f3c4eb18c7c1e5385ce19a92fb93b06cac851',oracle:'4bab9e030744a0887c1ee13bdd8fc8db3b98334f',nodes,functionCount:Object.keys(nodes).length,nodeCount:Object.values(nodes).reduce((a,b)=>a+b,0),generatedSha256:hash(artifact.source),nodeVersion:process.version},null,2));await s.call('close')}
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const metadata=JSON.parse(await readFile(join(dir,'authoring.json'),'utf8')),declarations=await s.call('inspect',{view:'declarations'})
 const regenerated=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 assert.equal(hash(regenerated.source),metadata.generatedSha256);assert.deepEqual(regenerated.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 const snapshotSha256=hash(await readFile(s.snapshot)),evidence=[]
 for(const c of JSON.parse(await readFile(join(dir,'cases.json'),'utf8'))){const response=await s.exchange('request',{op:'runPureByName',arguments:{function:'followupRun',arguments:c.arguments}});evidence.push({checks:validate(c,response),response});console.log(`PASS: follow-up ${c.id}`)}
 assert.equal(hash(await readFile(s.snapshot)),snapshotSha256)
 await writeFile(join(dir,replay?'replay-evidence.json':'evidence.json'),JSON.stringify(evidence,null,2))
 await writeFile(join(dir,replay?'replay-identity.json':'bundle-identity.json'),JSON.stringify({snapshotSha256,generatedSha256:metadata.generatedSha256,reemissionMatches:true,symbolsSha256:hash(await readFile(join(dir,'symbols.json'))),casesSha256:hash(await readFile(join(dir,'cases.json'))),status:await s.call('inspect',{view:'status'})},null,2))
}finally{await s.exchange('close')}
