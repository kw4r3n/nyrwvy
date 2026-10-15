import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author,maxFrameBytes} from './author.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]??'artifacts/epoch/bundle')
const restorationDisabled=process.argv.includes('--restoration-disabled')
await mkdir(dir,{recursive:true})
if(!replay)assert.deepEqual(await readdir(dir),[],'fresh evidence directory required')
const s=await session('session',dir)
try{
 if(!replay){
  const {artifact,declarations,nodes}=await author(s,{gateDisabled:restorationDisabled})
  for(const [name,value] of Object.entries({'symbols.json':artifact.symbols,'declarations.json':declarations,'authoring.json':{base:'95b331ae008c1504ce3c6c1b0d5675bf43926445',baseTree:'7ced2a777227845abf39b45f2c3c14038d120295',maxFrameBytes,gateDisabled:restorationDisabled,nodes}}))await writeFile(join(dir,name),JSON.stringify(value,null,2)+'\n')
  await writeFile(join(dir,'Generated.cs'),artifact.source);await s.call('close')
 }
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 assert.equal(artifact.source,await readFile(join(dir,'Generated.cs'),'utf8'))
 assert.deepEqual(artifact.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 console.log('PASS: epoch graph saved/reopened with exact unedited re-emission')
}finally{await s.exchange('close')}
