import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author,maxFrameBytes} from './author.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]??'artifacts/bounded-input/bundle')
const relaxed=process.argv.includes('--relaxed')
await mkdir(dir,{recursive:true})
if(!replay)assert.deepEqual(await readdir(dir),[],'use a fresh evidence directory')
const s=await session('session',dir)
try{
 if(!replay){
  const {artifact,declarations,nodes}=await author(s,{relaxed})
  for(const [name,value] of Object.entries({'symbols.json':artifact.symbols,'declarations.json':declarations,'authoring.json':{base:'f62782acd06188f35ba4ccb4fd6c3517a261a956',baseTree:'1b5fe82c6d64bd7dfee934e7ff3afbdee0ae416a',dependency:'PR397 -> PR396 -> PR395, unmerged',maxFrameBytes:relaxed?2*1024*1024:maxFrameBytes,relaxed,nodes}}))await writeFile(join(dir,name),JSON.stringify(value,null,2)+'\n')
  await writeFile(join(dir,'Generated.cs'),artifact.source)
  await s.call('close')
 }
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const d=await s.call('inspect',{view:'declarations'})
 const a=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:d.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:d.capabilities.map(c=>c.id)}}})
 assert.equal(a.source,await readFile(join(dir,'Generated.cs'),'utf8'))
 assert.deepEqual(a.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 console.log('PASS: graph policy authored/committed, saved/reopened, exact unedited C# re-emission')
}finally{await s.exchange('close')}
