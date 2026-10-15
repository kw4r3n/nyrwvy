// Offline fixture observations, never recovery authority or a user repository.
import {readFile} from 'node:fs/promises'
import {join} from 'node:path'
import {slot,store} from './continuation-fixture.mjs'
import {hash,git,inspect} from './git-fixture.mjs'
export const interruptionKey='physical-continuation-disposition/v1'
export const storageContract='intact-flushed-single-writer-before-slot/v1'
export const interruptedAuthority={interruptedContinuationStorageContract:storageContract}
export async function continuationRequest(d){
 const map=await store(join(d.root,'state')),prior=map[slot('physical-disposition/v1/retain-id')]
 return {id:'continue-id',authorization:'offline-owner-continue',action:'continue-owned-tail',retentionRequest:JSON.stringify(d.retention),dispositionDigest:hash(prior),expectedState:JSON.parse(prior).state,observedStateDigest:JSON.parse(prior).stateDigest}
}
export function custody(map,intent){return intent.Commands.map((_,stage)=>{
 const key=`physical-continuation-child/v1/${intent.Id}/${stage}`,raw=map[slot(key)]
 if(raw===undefined)return {key,stage,status:'absent',invocation:'',launchDigest:'',witnessDigest:''}
 const child=JSON.parse(raw),w=map[slot('physical-join/v1/'+child.Invocation)]
 return {key,stage,status:child.State,invocation:child.Invocation,launchDigest:hash(raw),witnessDigest:w===undefined?'':hash(w)}
})}
export async function interruptionRequest(d,{id='retain-continuation-id',authorization='offline-owner-retain-continuation',storageName='state'}={}){
 const map=await store(join(d.root,storageName)),raw=map[slot('physical-continuation-intent/v1')],intent=JSON.parse(raw),p=JSON.parse(map[slot('physical-plan/v1/'+intent.Identity)]),physical=await inspect(d.f.root)
 const facts={Head:physical.head,Symbolic:git(d.f.root,['symbolic-ref','HEAD']),Refs:physical.refs,Objects:physical.objects.join('\n'),IndexBytes:physical.index,IndexEntries:git(d.f.root,['ls-files','--stage']),Worktree:'file.txt:'+physical.gitMode+':'+hash(physical.worktree)}
 const state=physical.head===p.Before.Head?'object-only':physical.worktree==='base\n'?'ref-only':'applied'
 const evidence=Object.fromEntries(Object.entries(map).filter(([k])=>k!==slot(interruptionKey)).map(([k,v])=>[k,hash(v)]))
 return {id,authorization,action:'retain-unresolved-continuation',continuationId:intent.Id,continuationRequest:intent.Request,continuationIntentDigest:hash(raw),custodyDigest:hash(JSON.stringify(custody(map,intent))),evidenceDigest:hash(JSON.stringify(evidence)),expectedState:state,observedStateDigest:hash(JSON.stringify(facts)),storageContract}
}
export async function seed(s,name,state='object-only',window='after-continuation-intent'){
 const d=await s.domain(name);await s.run(d,{prefix:true,crash:state==='object-only'?'after-git-object':'after-git-ref'});d.retention=await s.retain(d,state)
 d.q=await continuationRequest(d);d.original=await store(join(d.root,'state'))
 if(window!==null)await s.run(d,{admin:'continuation',request:d.q,settings:{dispositionAuthorization:'offline-owner-retain'},crash:window})
 return d
}
