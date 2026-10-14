import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join} from 'node:path'
import {additionalSetup,slot,store,line,hash,git,untouched,until,alive} from '../offline-session-additional-admission/fixture.mjs'
import {inspect} from '../offline-session-physical-git/git-fixture.mjs'
export {slot,store,line,hash,git,untouched,until,alive}
export const I='additional-disposition/v1/intent',R='additional-disposition/v1/receipt'
export async function setup(bundle,parent,epochParent,dispositionParent,out){
 const s=await additionalSetup(bundle,parent,epochParent,dispositionParent,out)
 async function pending(name,window='after-git-worktree'){
  const d=await s.seed(name);const accepted=await s.admit(d);assert.ok(accepted.events.some(e=>e.operation==='outcome'&&e.value==='ok:activated-additional-admission'))
  await s.run(d,{frames:[d.additional.memberRequest,line('wait-extra','wait',{target:Number(d.additional.next)})]});await writeFile(join(d.root,'commit-release'),'release')
  const killed=await s.run(d,{frames:[d.additional.integrationRequest],crash:window});d.current=killed.after;return d
 }
 async function request(d,storageName='state'){
  const map=await store(join(d.root,storageName)),accepted=map[slot('additional/v1/intent')],q=JSON.parse(accepted),token=s.token(d),registryRaw=map[slot('additional-registry/v1')],reg=JSON.parse(registryRaw),journalRaw=map[slot('additional-journal/v1/'+q.integrationIdentity)],planRaw=map[slot('physical-plan/v1/'+token)],plan=JSON.parse(planRaw),custody=[]
  for(let stage=0;stage<3;stage++){const key='physical-epoch-child/v1/'+hash(token)+'/'+stage,launch=map[slot(key)],child=JSON.parse(launch),witness=map[slot('physical-join/v1/'+child.Invocation)];custody.push({key,stage,launch,witness,launchDigest:hash(launch),witnessDigest:hash(witness)})}
  const inventory=Object.fromEntries(Object.entries(map).filter(([k])=>![slot(I),slot(R)].includes(k)).sort(([a],[b])=>a.localeCompare(b)).map(([k,v])=>[k,hash(v)])),physical=await inspect(d.f.root)
  const current={Head:physical.head,Symbolic:git(d.f.root,['symbolic-ref','HEAD']),Refs:physical.refs,Objects:physical.objects.join('\n'),IndexBytes:physical.index,IndexEntries:git(d.f.root,['ls-files','--stage']),Worktree:'file.txt:'+physical.gitMode+':'+hash(physical.worktree)}
  return {id:'retain-additional-id',authorization:'offline-owner-retain-additional',action:'retain-unresolved-additional-integration',scope:'one-applied-owned-additional-integration/v1',predecessorDigest:hash(map[slot('epoch-disposition/v1/receipt')]),additionalRequest:accepted,activation:map[slot('additional/v1/activation')],blockedDigest:hash(map[slot('additional/v1/blocked')]),registryDigest:hash(registryRaw),journalDigest:hash(journalRaw),planDigest:hash(planRaw),custodyDigest:hash(JSON.stringify(custody)),inventoryDigest:hash(JSON.stringify(inventory)),newEffectState:'applied',currentStateDigest:hash(JSON.stringify(current)),repository:d.f.root,repositoryId:q.repositoryId,integrationRequest:q.integrationRequest,integrationIdentity:q.integrationIdentity,integrationTypedId:JSON.stringify(JSON.parse(q.integrationRequest).id),effectIdentity:token,member:String(JSON.parse(plan.Intent).target),next:reg.next,fingerprint:reg.fingerprint,intent:reg.intent}
 }
 const decide=(d,q,o={})=>s.run(d,{...o,admin:'additionalDisposition',request:q,settings:{additionalDispositionAuthorization:typeof q==='string'?'offline-owner-retain-additional':q.authorization,...o.settings}})
 return {...s,pending,request,decide}
}
