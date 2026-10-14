import assert from 'node:assert/strict'
import {readFile,writeFile,mkdir,readdir} from 'node:fs/promises'
import {join} from 'node:path'
import {suite as parentSuite,slot,store,line,untouched,uncertain,until,alive} from '../offline-session-physical-git/continuation-fixture.mjs'
import {suite} from './fixture-process.mjs'
import {continuationRequest,interruptionRequest,interruptedAuthority} from '../offline-session-physical-git/interruption-fixture.mjs'
import {hash,git} from '../offline-session-physical-git/git-fixture.mjs'
export {slot,store,line,untouched,uncertain,until,alive,hash,git}
export async function setup(bundle,parent,epochParent,out){
 const inherited=await parentSuite(parent,join(out,'parent')),prior=await suite(epochParent,join(out,'epoch-parent')),s=await suite(bundle,join(out,'disposition'))
 async function bytes(root){const files={};async function walk(dir){for(const e of (await readdir(dir,{withFileTypes:true})).sort((a,b)=>a.name.localeCompare(b.name))){const path=join(dir,e.name);if(e.isDirectory())await walk(path);else files[path.slice(root.length+1)]=(await readFile(path)).toString('base64')}}await walk(root);return files}
 for(const fixture of [inherited,prior,s]){const original=fixture.run;fixture.run=async(d,o)=>{const before=await bytes(d.f.root),r=await original(d,o);r.repositoryBytesBefore=before;r.repositoryBytesAfter=await bytes(d.f.root);await writeFile(join(d.root,`row-${r.index}.json`),JSON.stringify(r,null,2)+'\n');return r}}
async function seed(name,expectedState='applied'){
 const d=await inherited.domain(name)
 // The additional selected object is provisioned before either old request.
 // It therefore belongs to the frozen original object inventory, not injected
 // recovery state or a command performed by administrative admission.
 await writeFile(join(d.f.root,'file.txt'),'renewed\n');git(d.f.root,['add','file.txt']);const tree=git(d.f.root,['write-tree']);d.selected=git(d.f.root,['commit-tree',tree,'-p',d.f.selected],'renewed selected\n');git(d.f.root,['read-tree','--reset','-u',d.f.base])
 await inherited.run(d,{prefix:true,crash:'after-git-object'});d.retention=await inherited.retain(d,'object-only');d.continuation=await continuationRequest(d)
 await inherited.run(d,{admin:'continuation',request:d.continuation,settings:{dispositionAuthorization:'offline-owner-retain'},crash:expectedState==='applied'?'before-continuation-result':expectedState==='ref-only'?'after-continuation-mutation-0':'after-continuation-intent'})
 d.interruption=await interruptionRequest(d);assert.equal(d.interruption.expectedState,expectedState)
 const retained=await inherited.run(d,{admin:'interruptedContinuation',request:d.interruption,settings:interruptedAuthority});assert.deepEqual(retained.outputs,['ok:retained-unresolved-continuation:'+expectedState])
 d.frozen=await store(join(d.root,'state'));const r=JSON.parse(d.frozen[slot('physical-continuation-disposition/v1')]),old=JSON.parse(d.frozen[slot('registry/v1')]),memberRequest=line('fresh-member','begin',{target:Number(old.next),mode:'spawn'}),integrationRequest=line('fresh-integration','integrate',{target:Number(old.next)})
 d.request={id:'admit-id',epoch:'renewed-epoch',authorization:'offline-owner-admit',action:'activate-renewed-admission',scope:'one-fresh-member-one-synchronous-integration/v1',predecessorDigest:hash(d.frozen[slot('physical-continuation-disposition/v1')]),inventoryDigest:r.evidenceDigest,baselineStateDigest:r.stateDigest,baselineHead:git(d.f.root,['rev-parse','HEAD']),repository:d.f.root,repositoryId:await readFile(join(d.f.root,'.git','offline-fixture-id'),'utf8'),originalRequest:line('original-id','integrate',{target:1}),memberRequest,integrationRequest,memberIdentity:hash(JSON.stringify('fresh-member')),integrationIdentity:hash(JSON.stringify('fresh-integration')),selectedCommit:d.selected}
 d.patch=git(d.f.root,['diff',d.request.baselineHead,d.selected,'--','file.txt']);return d
}
const settings=d=>({...interruptedAuthority,epochAuthorization:d.request.authorization,dispositionAuthorization:'offline-owner-retain',continuationAuthorization:'offline-owner-continue',interruptedContinuationAuthorization:'offline-owner-retain-continuation',snapshotCommit:d.selected,patch:d.patch,providers:{1:'complete',2:'complete'},gitOwner:new URL('../offline-session-epoch/git-owner.mjs',import.meta.url).pathname})

 const run=(d,o={})=>s.run(d,{...o,settings:{...settings(d),...o.settings}})
 const runPrior=(d,o={})=>prior.run(d,{...o,settings:{...settings(d),...o.settings}})
 async function activate(d){const r=await runPrior(d,{admin:'epoch',request:d.request});assert.ok(r.events.some(e=>e.operation==='outcome'&&e.isOk));return r}
 async function pending(d,window='before-epoch-registry-publication'){
  await activate(d);await runPrior(d,{frames:[d.request.memberRequest,line('wait-fresh','wait',{target:2})]});await writeFile(join(d.root,'commit-release'),'release')
  const killed=await runPrior(d,{frames:[d.request.integrationRequest],crash:window});d.current=await store(join(d.root,'state'));return killed
 }
 const token=d=>'journal:'+d.request.integrationIdentity+':'+hash(d.request.integrationRequest)
 const request=async d=>{
  const map=await store(join(d.root,'state')),planRaw=map[slot('physical-plan/v1/'+token(d))],registryRaw=map[slot('epoch-registry/v1')],journalRaw=map[slot('epoch-journal/v1/'+d.request.integrationIdentity)]
  const plan=JSON.parse(planRaw),reg=JSON.parse(registryRaw),custody=[]
  for(let stage=0;stage<3;stage++){const key='physical-epoch-child/v1/'+hash(token(d))+'/'+stage,launch=map[slot(key)],child=JSON.parse(launch),witness=map[slot('physical-join/v1/'+child.Invocation)];custody.push({key,stage,launch,witness,launchDigest:hash(launch),witnessDigest:hash(witness)})}
  const inventory=Object.fromEntries(Object.entries(map).filter(([key])=>![slot('epoch-disposition/v1/intent'),slot('epoch-disposition/v1/receipt')].includes(key)).sort(([a],[b])=>a.localeCompare(b)).map(([key,v])=>[key,hash(v)]))
  const inspect=await import('../offline-session-physical-git/git-fixture.mjs'),physical=await inspect.inspect(d.f.root)
  // Match the mechanical GitState encoding, including retained raw index bytes.
  const current={Head:physical.head,Symbolic:git(d.f.root,['symbolic-ref','HEAD']),Refs:physical.refs,Objects:physical.objects.join('\n'),IndexBytes:physical.index,IndexEntries:git(d.f.root,['ls-files','--stage']),Worktree:'file.txt:'+physical.gitMode+':'+hash(physical.worktree)}
  return {id:'retain-fresh-id',authorization:'offline-owner-retain-fresh',action:'retain-unresolved-epoch-integration',scope:'one-applied-owned-fresh-integration/v1',epochRequest:map[slot('epoch/v1/intent')],activation:map[slot('epoch/v1/activation')],blockedDigest:hash(map[slot('epoch/v1/blocked')]),registryDigest:hash(registryRaw),journalDigest:hash(journalRaw),planDigest:hash(planRaw),custodyDigest:hash(JSON.stringify(custody)),inventoryDigest:hash(JSON.stringify(inventory)),newEffectState:'applied',currentStateDigest:hash(JSON.stringify(current)),repository:d.f.root,repositoryId:d.request.repositoryId,integrationRequest:d.request.integrationRequest,integrationIdentity:d.request.integrationIdentity,integrationTypedId:JSON.stringify(JSON.parse(d.request.integrationRequest).id),effectIdentity:token(d),member:String(JSON.parse(plan.Intent).target),next:reg.next,fingerprint:reg.fingerprint,intent:reg.intent}
 }
 const decide=(d,q,o={})=>run(d,{...o,admin:'epochDisposition',request:q,settings:{epochDispositionAuthorization:q.authorization,...o.settings}})
 async function save(){for(const [name,x] of [['parent',inherited],['epoch-parent',prior],['disposition',s]])await writeFile(join(out,name,'raw.json'),JSON.stringify(x.rows,null,2)+'\n')}
 return {inherited,prior,s,seed,settings,run,runPrior,activate,pending,token,request,decide,save}
}
