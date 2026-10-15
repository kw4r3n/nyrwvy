import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author,maxFrameBytes} from './author.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]??'artifacts/reader-propagation/bundle')
const deferred=process.argv.includes('--deferred')
await mkdir(dir,{recursive:true})
if(!replay)assert.deepEqual(await readdir(dir),[],'fresh evidence directory required')
const s=await session('session',dir)
try{
 if(!replay){
  const {artifact,declarations,nodes}=await author(s,{deferred})
  for(const [name,value] of Object.entries({'symbols.json':artifact.symbols,'declarations.json':declarations,'authoring.json':{base:'43507a5aee1aa0f706ec4b86e19392936fdcd94a',baseTree:'a5ac4707f764dcd2219e7d16badb57a29f5b18c3',maxFrameBytes,deferred,nodes}}))await writeFile(join(dir,name),JSON.stringify(value,null,2)+'\n')
  await writeFile(join(dir,'Generated.cs'),artifact.source);await s.call('close')
 }
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 assert.equal(artifact.source,await readFile(join(dir,'Generated.cs'),'utf8'))
 assert.deepEqual(artifact.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 console.log('PASS: ordinary graph saved/reopened with exact unedited re-emission')
}finally{await s.exchange('close')}
