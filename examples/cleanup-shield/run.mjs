import assert from 'node:assert/strict'
import {mkdir,readFile,writeFile} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {randomUUID,createHash} from 'node:crypto'
import {openAuthoring,requestAuthoring,closeAuthoring} from '../../tools/nyrwvy-agent/authoring.ts'
import {define} from './author.mjs'
import {cases,argsFor,normalize} from './cases.mjs'
const dir=resolve(process.argv[2]),replay=process.argv.includes('--replay'),snapshot=join(dir,'shield.nyrec'),hash=x=>createHash('sha256').update(x).digest('hex')
await mkdir(dir,{recursive:true})
const rootArgument=process.argv.indexOf('--authoring-root')
const context={sessionID:randomUUID(),worktree:rootArgument<0?resolve('.'):resolve(process.argv[rootArgument+1])}
const request=async payload=>JSON.parse(await requestAuthoring(payload,context))
const success=async payload=>{const reply=await request(payload);assert.equal(reply.ok,true,JSON.stringify(reply));return reply.result}
const publish=async payload=>{const result=await success(payload);assert.equal(result.review.readiness.kind,'semanticallyReady',JSON.stringify(result));await success({op:'commit',proposalId:result.proposalId})}
const create=(displayName,params,resultType,body,requiredEffects=[])=>publish({op:'simulateCreateFunctionByName',displayName,parameters:Object.entries(params).map(([displayName,type])=>({displayName,type})),resultType,body,requiredEffects})
try {
  await openAuthoring(replay?'open':'create',snapshot,context)
  if(!replay) await define(create,publish,{shieldedSurface:!process.argv.includes('--baseline')})
  const declarations=await success({op:'declarations'}),probe=declarations.capabilities.find(c=>c.displayName==='Probe')
  const profile={kind:'externalImplementations',operationIds:probe.operations.map(x=>x.id),concurrentCapabilityIds:[probe.id]}
  const artifact=await success({op:'emitCSharp',profile})
  if(!replay) {
    await writeFile(join(dir,'Generated.cs'),artifact.source)
    await writeFile(join(dir,'symbols.json'),JSON.stringify(artifact.symbols,null,2))
    await writeFile(join(dir,'declarations.json'),JSON.stringify(declarations,null,2))
    await writeFile(join(dir,'cases.json'),JSON.stringify(cases,null,2))
  } else {
    assert.equal(artifact.source,await readFile(join(dir,'Generated.cs'),'utf8'))
    assert.deepEqual(artifact.symbols,JSON.parse(await readFile(join(dir,'symbols.json'),'utf8')))
  }
  await closeAuthoring(context)
  await openAuthoring('open',snapshot,context)
  assert.deepEqual(await success({op:'emitCSharp',profile}),artifact)
  const before=hash(await readFile(snapshot)),results=[],baseline=[]
  for(const c of cases) {
    for(const protected_ of [true,false]) {
      const reply=await success({op:'runPureByName',function:'run',arguments:argsFor(c,protected_)})
      const row=normalize(c,reply.calls)
      ;(protected_?results:baseline).push(row)
    }
    console.log(`PASS: virtual/reopened ${c.id}`)
  }
  assert.equal(hash(await readFile(snapshot)),before)
  await writeFile(join(dir,replay?'replay-evidence.json':'evidence.json'),JSON.stringify({results,baseline},null,2))
  await writeFile(join(dir,'identity.json'),JSON.stringify({snapshotSha256:before,generatedSha256:hash(artifact.source),symbolsSha256:hash(JSON.stringify(artifact.symbols)),schemaVersion:Number((await readFile(snapshot,'utf8')).match(/^snapshot \S+ schema=(\d+)/)[1])},null,2))
} finally {await closeAuthoring(context).catch(()=>{})}
