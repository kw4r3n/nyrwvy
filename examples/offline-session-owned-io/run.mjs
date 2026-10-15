import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author} from './author.mjs'
import {author as serialAuthor} from '../offline-session-transport/author.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]??'artifacts/transport/bundle')
const sequential=process.argv.includes('--sequential')
await mkdir(dir,{recursive:true})
if(!replay)assert.deepEqual(await readdir(dir),[],'use a fresh evidence directory')
const s=await session('session',dir)
try{
 if(!replay){
  const {artifact,declarations,nodes}=await (sequential?serialAuthor:author)(s)
  for(const [name,value] of Object.entries({'symbols.json':artifact.symbols,'declarations.json':declarations,'authoring.json':{base:'b655d24ff70f67334cdbc9d43c40a0ddf64d1fe8',dependency:'PR397 stacked on PR396 -> PR395, unmerged',sequential,nodes}}))await writeFile(join(dir,name),JSON.stringify(value,null,2)+'\n')
  await writeFile(join(dir,'Generated.cs'),artifact.source)
  await s.call('close')
 }
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const d=await s.call('inspect',{view:'declarations'})
 const a=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:d.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:d.capabilities.map(c=>c.id)}}})
 assert.equal(a.source,await readFile(join(dir,'Generated.cs'),'utf8'))
 assert.deepEqual(a.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 console.log('PASS: authored, committed, saved/reopened, exact unedited C# re-emission')
}finally{await s.exchange('close')}
