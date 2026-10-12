import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import assert from 'node:assert/strict'
import {createHash} from 'node:crypto'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author} from './author.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]),hash=x=>createHash('sha256').update(x).digest('hex')
await mkdir(dir,{recursive:true})
const s=await session('exceptions',dir)
try{
 if(!replay){
  assert.deepEqual(await readdir(dir),[])
  const {artifact,declarations,nodes}=await author(s)
  for(const [name,value] of Object.entries({'Generated.cs':artifact.source,'symbols.json':JSON.stringify(artifact.symbols,null,2),'declarations.json':JSON.stringify(declarations,null,2),'authoring.json':JSON.stringify({nodes},null,2)}))await writeFile(join(dir,name),value)
  await s.call('close')
 }
 await s.call('open',{mode:'open',snapshot:s.snapshot})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 assert.equal(artifact.source,await readFile(join(dir,'Generated.cs'),'utf8'))
 assert.deepEqual(artifact.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
 const before=hash(await readFile(s.snapshot)),rows=[]
 const host={kind:'fakeCapability',capability:'Probe',operations:[{operation:'step',responses:[{kind:'throw',message:'fault'}]},{operation:'report',responses:[{kind:'return',value:{kind:'unit'}},{kind:'return',value:{kind:'unit'}}]}]}
 for(const [fn,expected,throws] of [['faultOuter','fault',false],['faultCatch','fault',true],['abortCatch','typed abort',false],['contextFault','fault',true]]){
  const response=await s.exchange('request',{op:'runPureByName',arguments:{function:fn,arguments:fn==='faultCatch'?[host,{kind:'unit'}]:[host]}})
  assert.equal(response.ok,!throws,JSON.stringify(response))
  const record=response.result??response.error
  if(!throws)assert.equal(record.actual.value,expected)
  else assert.match(record.message,new RegExp(expected))
  if(fn==='abortCatch')assert.equal(record.calls.length,0,'typed abort never calls host handler')
  if(fn==='contextFault')assert.equal(record.calls.filter(c=>c.operation==='report').length,0,'context fault is outside catch')
  const {id:requestId,...stableResponse}=response
  rows.push({function:fn,response:stableResponse})
 }
 assert.equal(hash(await readFile(s.snapshot)),before)
 await writeFile(join(dir,replay?'replay-evidence.json':'evidence.json'),JSON.stringify(rows,null,2))
 await writeFile(join(dir,'identity.json'),JSON.stringify({snapshotSha256:before,generatedSha256:hash(artifact.source)},null,2))
 console.log(`PASS: four generic virtual cases, reopen and identical emission; ${dir}`)
}finally{await s.exchange('close')}
