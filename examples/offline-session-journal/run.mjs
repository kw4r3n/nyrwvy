import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author,maxFrameBytes} from './author.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]??'artifacts/journal/bundle')
const disabled=process.argv.includes('--disabled')
const receiptDisabled=process.argv.includes('--receipt-disabled')
await mkdir(dir,{recursive:true})
if(!replay)assert.deepEqual(await readdir(dir),[],'fresh evidence directory required')
const s=await session('session',dir)
try{
 if(!replay){
  const {artifact,declarations,nodes}=await author(s,{disabled,receiptDisabled})
  for(const [name,value] of Object.entries({'symbols.json':artifact.symbols,'declarations.json':declarations,'authoring.json':{base:'6f4c2c3a8e9dea07ff992de1b29190e82d5bb19e',baseTree:'5c77e7f281a48c1ffdfffca4ce3f23135c5d47d1',maxFrameBytes,disabled,receiptDisabled,nodes}}))await writeFile(join(dir,name),JSON.stringify(value,null,2)+'\n')
  await writeFile(join(dir,'Generated.cs'),artifact.source);await s.call('close')
 }
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 assert.equal(artifact.source,await readFile(join(dir,'Generated.cs'),'utf8'))
 assert.deepEqual(artifact.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 console.log('PASS: journal graph saved/reopened with exact unedited re-emission')
}finally{await s.exchange('close')}
