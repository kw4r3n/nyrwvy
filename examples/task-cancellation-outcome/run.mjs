import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
import {session,flat,returned,unit,string} from '../dynamic-worker-collector/lib.mjs'
import {author} from './author.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]),hash=x=>createHash('sha256').update(x).digest('hex')
await mkdir(dir,{recursive:true})
const s=await session('task-outcomes',dir)
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
 // Fake throws are ordinary host exceptions. They do not prove OCE classification.
 for(const fn of ['baseline','waitOnly','adapted','cleanup'])for(const kind of ['ok','err','throw']){
  const host={kind:'fakeCapability',capability:'Probe',operations:[
   {operation:'work',responses:[kind==='throw'?{kind:'throw',message:'operation cancelled'}:returned(flat(kind==='ok'?'success':'existing-error',kind==='ok'))]},
   {operation:'open',responses:[returned({kind:'result',okType:unit,errorType:string,isOk:true,value:{kind:'unit'}})]},
   {operation:'close',responses:[returned({kind:'unit'})]},
   {operation:'cleanup',responses:[returned({kind:'unit'})]}
  ]}
  const response=await s.exchange('request',{op:'runPureByName',arguments:{function:fn,arguments:[host,{kind:'bool',value:false}]}})
  assert.equal(response.ok,kind!=='throw',JSON.stringify(response))
  if(kind!=='throw'){
   assert.equal(response.result.actual.isOk,kind==='ok')
   assert.equal(response.result.actual.value,kind==='ok'?'success':'existing-error')
   assert.equal(response.result.calls.filter(x=>x.operation==='close').length,1)
   assert.equal(response.result.calls.filter(x=>x.operation==='cleanup').length,fn==='cleanup'?1:0)
  }else assert.match(response.error.message,/operation cancelled/)
  const {id,...stable}=response;rows.push({fn,kind,response:stable})
 }
 assert.equal(hash(await readFile(s.snapshot)),before)
 await writeFile(join(dir,replay?'replay-evidence.json':'evidence.json'),JSON.stringify(rows,null,2)+'\n')
 await writeFile(join(dir,'identity.json'),JSON.stringify({snapshotSha256:before,generatedSha256:hash(artifact.source)},null,2)+'\n')
 console.log(`PASS: ${rows.length} virtual controls; save/reopen and identical emission; ${dir}`)
}finally{await s.exchange('close')}
