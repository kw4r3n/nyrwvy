import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {createHash} from 'node:crypto'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author} from './author.mjs'
import {scenarios,parserCases} from './cases.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]??'artifacts/offline-leader-jsonrpc/bundle')
const hash=x=>createHash('sha256').update(x).digest('hex')
await mkdir(dir,{recursive:true})
if(!replay)assert.deepEqual(await readdir(dir),[],'use a fresh evidence directory')
const s=await session('session',dir)
try{
 if(!replay){
  const {artifact,declarations,nodes}=await author(s)
  for(const [name,value] of Object.entries({'symbols.json':JSON.stringify(artifact.symbols,null,2),'declarations.json':JSON.stringify(declarations,null,2),'authoring.json':JSON.stringify({base:'e2fc3a0488171d9978a781f519c7a0dca20e1206',nodes},null,2),'cases.json':JSON.stringify(scenarios(),null,2),'parser-cases.json':JSON.stringify(parserCases(),null,2)}))await writeFile(join(dir,name),value+'\n')
  // Source bytes are the unedited projection; avoid even a newline addition.
  await writeFile(join(dir,'Generated.cs'),artifact.source)
  await s.call('close')
 }
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 assert.equal(artifact.source,await readFile(join(dir,'Generated.cs'),'utf8'))
 assert.deepEqual(artifact.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 const before=hash(await readFile(s.snapshot)),rows=[]
 const cases=JSON.parse(await readFile(join(dir,'parser-cases.json'),'utf8'))
 const response=await s.exchange('request',{op:'runPureByName',arguments:{function:'wirePreview',arguments:[{kind:'list',itemKind:'string',items:cases.map(c=>c.source)}]}})
 const stable=(({id,...rest})=>rest)(response)
 assert.equal(response.ok,true,JSON.stringify(response))
 assert.deepEqual(response.result.actual.items,cases.map(c=>c.expected),'parser/envelope/binding exact outputs')
 rows.push({id:'wire-parser-cases',response:stable,count:cases.length})
 assert.equal(hash(await readFile(s.snapshot)),before)
 await writeFile(join(dir,replay?'replay-evidence.json':'evidence.json'),JSON.stringify(rows,null,2)+'\n')
 const identity={snapshotSha256:before,generatedSha256:hash(artifact.source),symbolsSha256:hash(await readFile(join(dir,'symbols.json'))),casesSha256:hash(await readFile(join(dir,'cases.json'))),parserCasesSha256:hash(await readFile(join(dir,'parser-cases.json'))),declarationsSha256:hash(await readFile(join(dir,'declarations.json'))),reemissionMatches:true,status:await s.call('inspect',{view:'status'})}
 await writeFile(join(dir,replay?'replay-identity.json':'identity.json'),JSON.stringify(identity,null,2)+'\n')
 console.log(`PASS: ordinary authoring, save/reopen, ${cases.length} exact parser outputs; ${dir}`)
}finally{await s.exchange('close')}
