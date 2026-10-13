import assert from 'node:assert/strict'
import {mkdir,readdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {createHash} from 'node:crypto'
import {session} from '../dynamic-worker-collector/lib.mjs'
import {author} from './author.mjs'
import {scenarios,argumentsFor,acceptance} from './cases.mjs'
const replay=process.argv[2]==='--replay',dir=resolve(process.argv[replay?3:2]??'artifacts/offline-leader-team/bundle')
const hash=x=>createHash('sha256').update(x).digest('hex')
await mkdir(dir,{recursive:true})
if(!replay)assert.deepEqual(await readdir(dir),[],'use a fresh evidence directory')
const s=await session('session',dir)
try{
 if(!replay){
  const {artifact,declarations,nodes}=await author(s)
  for(const [name,value] of Object.entries({'symbols.json':JSON.stringify(artifact.symbols,null,2),'declarations.json':JSON.stringify(declarations,null,2),'authoring.json':JSON.stringify({base:'7f4dc33a33db3c9079359b04a851dc7010f0d784',nodes},null,2),'cases.json':JSON.stringify(scenarios(),null,2)}))await writeFile(join(dir,name),value+'\n')
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
 // One virtual session proves persistence and execution. Real gates prove both
 // cancellation forms and faults; fake exceptions cannot prove OCE identity.
 const c=JSON.parse(await readFile(join(dir,'cases.json'),'utf8')).find(x=>x.id==='leader-session-none')
 const response=await s.exchange('request',{op:'runPureByName',arguments:{function:'sessionRun',arguments:argumentsFor(c)}})
 const stable=(({id,...rest})=>rest)(response)
 const row={id:c.id,response:stable};row.checks=acceptance(c,row);rows.push(row)
 assert.equal(hash(await readFile(s.snapshot)),before)
 await writeFile(join(dir,replay?'replay-evidence.json':'evidence.json'),JSON.stringify(rows,null,2)+'\n')
 const identity={snapshotSha256:before,generatedSha256:hash(artifact.source),symbolsSha256:hash(await readFile(join(dir,'symbols.json'))),casesSha256:hash(await readFile(join(dir,'cases.json'))),reemissionMatches:true,status:await s.call('inspect',{view:'status'})}
 await writeFile(join(dir,replay?'replay-identity.json':'identity.json'),JSON.stringify(identity,null,2)+'\n')
 console.log(`PASS: ordinarily authored session, save/reopen, virtual execution; ${dir}`)
}finally{await s.exchange('close')}
