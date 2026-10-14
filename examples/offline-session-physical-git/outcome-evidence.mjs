import assert from 'node:assert/strict'
import {hash} from './git-fixture.mjs'
// Child observations cannot authenticate an original request result. For an
// active overflow/drain failure, assert absence of every actual publication.
export function assertMissingActualOutcome(row,{wire=false}={}){
 const state=row.after??row.storageAfter,key=k=>hash(k)+'.json'
 const child=JSON.parse(state[key('physical-child/v1')]),token=child.Identity
 assert.equal(child.State,'active');assert.equal(child.OutputDrained,false)
 assert.equal(state[key('outcome-receipt/'+token)],undefined,'no actual outcome receipt')
 assert.equal(state[key('outcome/'+token)],undefined,'no separate actual outcome')
 const journal=Object.values(state).map(x=>{try{return JSON.parse(x)}catch{return null}}).find(x=>x?.effectIdentity===token)
 assert.ok(journal,'pending request journal retained');assert.equal(journal.state,'pending')
 assert.equal(journal.outcomeSource,'');assert.equal(journal.outcome,'')
 const registry=JSON.parse(state[key('registry/v1')]);assert.equal(registry.phase,'idle');assert.equal(registry.outcomeSource,'');assert.equal(registry.outcome,'')
 assert.ok(!row.events.some(e=>['storage-write-attempt','storage-written'].includes(e.operation)&&(e.key?.startsWith('outcome-receipt/')||e.key?.startsWith('outcome/'))),'no attempted or completed actual-outcome write')
 assert.ok(!row.events.some(e=>['storage-write-attempt','storage-written'].includes(e.operation)&&e.key==='registry/v1'&&JSON.parse(e.value).phase==='ready'),'no ready actual publication')
 if(wire){const outcomes=row.outputs.map(JSON.parse).filter(x=>x.id==='same-integration-id').map(x=>x.result?.outcome);assert.deepEqual(outcomes,['err:unresolved'])}
 return {fenceState:child.State,outputDrained:child.OutputDrained,journalState:journal.state,registryPhase:registry.phase,actualOutcomeReceiptPresent:false,actualOutcomePresent:false,actualOutcomeWrites:0}
}
