import assert from 'node:assert/strict'
import { resolve } from 'node:path'
import { writeFile } from 'node:fs/promises'
import * as z from 'zod/v4'
import { createGraphTools } from '../../tools/nyrwvy-agent/graph-tools.mjs'
const tools=createGraphTools(z),context={sessionID:`oneshot-red-${process.pid}`,worktree:resolve('.')}
const primitive={kind:'primitive',primitive:'string'}
try {
 const opened=JSON.parse(await tools.open.execute({mode:'create',snapshot:resolve(process.argv[2])},context));assert.notEqual(opened.ok,false,JSON.stringify(opened))
 const proposal=JSON.parse(await tools.create_function.execute({displayName:'makeCell',parameters:[{displayName:'scope',type:{kind:'builtinGeneric',generic:'taskScope',arguments:[]}}],resultType:{kind:'builtinGeneric',generic:'oneShot',arguments:[primitive]},requiredEffects:[],body:{kind:'oneShotCreate',scope:{kind:'parameter',name:'scope'},valueType:primitive}},context))
 assert.notEqual(proposal.ok,false,'Desired ordinary authoring of a typed scoped one-shot: '+JSON.stringify(proposal))
 assert.equal(proposal.result.review.readiness.kind,'semanticallyReady')
 const committed=JSON.parse(await tools.commit.execute({proposalId:proposal.result.proposalId},context));assert.notEqual(committed.ok,false,JSON.stringify(committed))
 await tools.close.execute({},context)
 const reopened=JSON.parse(await tools.open.execute({mode:'open',snapshot:resolve(process.argv[2])},context));assert.notEqual(reopened.ok,false,JSON.stringify(reopened))
 const emitted=JSON.parse(await tools.request.execute({op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:[]}}},context));assert.notEqual(emitted.ok,false,JSON.stringify(emitted))
 await writeFile(resolve(process.argv[2])+'.cs',emitted.result.source)
 console.log('PASS: ordinary one-shot author/review/commit/save/reopen/emit')
} finally {await tools.close.execute({},context)}
