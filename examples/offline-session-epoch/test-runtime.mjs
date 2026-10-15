// Controlled offline single-writer domains only. Parent evidence remains intact.
import assert from 'node:assert/strict'
import {readFile,writeFile,mkdir,cp,chmod,readdir,rm} from 'node:fs/promises'
import {join} from 'node:path'
import {suite as parentSuite,slot,store,line,untouched,uncertain,until,alive} from '../offline-session-physical-git/continuation-fixture.mjs'
import {suite} from './fixture.mjs'
import {continuationRequest,interruptionRequest,interruptedAuthority} from '../offline-session-physical-git/interruption-fixture.mjs'
import {hash,git} from '../offline-session-physical-git/git-fixture.mjs'
const [bundle,parent,out]=process.argv.slice(2)
const inherited=await parentSuite(parent,join(out,'parent')),s=await suite(bundle,join(out,'epoch')),contracts=[]
async function bytes(root){const files={};async function walk(dir){for(const e of (await readdir(dir,{withFileTypes:true})).sort((a,b)=>a.name.localeCompare(b.name))){const path=join(dir,e.name);if(e.isDirectory())await walk(path);else files[path.slice(root.length+1)]=(await readFile(path)).toString('base64')}}await walk(root);return files}
for(const fixture of [inherited,s]){const original=fixture.run;fixture.run=async(d,o)=>{const before=await bytes(d.f.root),r=await original(d,o);r.repositoryBytesBefore=before;r.repositoryBytesAfter=await bytes(d.f.root);await writeFile(join(d.root,`row-${r.index}.json`),JSON.stringify(r,null,2)+'\n');return r}}
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
const settings=d=>({...interruptedAuthority,epochAuthorization:d.request.authorization,dispositionAuthorization:'offline-owner-retain',continuationAuthorization:'offline-owner-continue',interruptedContinuationAuthorization:'offline-owner-retain-continuation',snapshotCommit:d.selected,patch:d.patch,providers:{1:'complete',2:'complete'},gitOwner:new URL('./git-owner.mjs',import.meta.url).pathname})
const run=(d,o={})=>s.run(d,{...o,settings:{...settings(d),...o.settings}})
const activate=(d,q=d.request,o={})=>run(d,{...o,admin:'epoch',request:q})
function preserved(d,r){for(const [k,v] of Object.entries(d.frozen))assert.equal(r.after[k],v,k);uncertain(r.after);assert.equal(r.after[slot('physical-continuation-result/v1/continue-id')],undefined)}
async function rejected(d,q,o={}){const r=await activate(d,q,o);assert.ok(r.events.some(e=>e.operation==='outcome'&&e.value==='unresolved'),JSON.stringify(r.events.slice(-5)));untouched(r);preserved(d,r);return r}
async function closed(d,o={}){const r=await run(d,o);untouched(r);assert.ok(!r.events.some(e=>e.operation==='read-start'));preserved(d,r);return r}
async function admitted(d){const r=await activate(d);assert.ok(r.events.some(e=>e.operation==='outcome'&&e.value==='ok:activated-renewed-admission'),JSON.stringify(r.events.slice(-5)));assert.deepEqual(r.physicalBefore,r.physicalAfter);preserved(d,r);assert.ok(!r.events.some(e=>e.operation==='physical-git-owner-start'||e.operation==='read-start'));return r}
async function blocked(d){const r=await run(d,{frames:[d.request.originalRequest,line('original-id','integrate',{target:2}),line('distinct-old','integrate',{target:1})]});preserved(d,r);assert.ok(r.outputs.some(x=>x.includes('err:payload-conflict')));assert.ok(!r.events.some(e=>e.operation==='physical-git-owner-start'));return r}
const d=await seed('success');await closed(d);await admitted(d);const replay=await activate(d);untouched(replay);await blocked(d)
await writeFile(join(d.root,'commit-release'),'release')
const actual=await run(d,{frames:[line('inspect-old','inspect'),d.request.memberRequest,line('wait-fresh','wait',{target:2}),d.request.integrationRequest]});preserved(d,actual)
assert.equal(actual.events.filter(e=>e.operation==='physical-git-owner-start').length,3,JSON.stringify(actual.events.slice(-15)));const publication=JSON.parse(actual.after[slot('epoch-registry/v1')]);assert.equal(publication.outcomeSource,'actual');assert.equal(publication.outcome,'ok:'+actual.physicalAfter.head);assert.equal(publication.entries[0].integrated,'');assert.equal(publication.entries[1].base,d.request.baselineHead);assert.equal(publication.entries[1].integrated,actual.physicalAfter.head)
const lost=await run(d,{frames:[d.request.memberRequest,d.request.integrationRequest]});preserved(d,lost);assert.ok(!lost.events.some(e=>e.operation==='physical-git-owner-start'||e.operation==='provider'||e.operation==='workspace-create'))
await blocked(d);contracts.push({name:'one-activation-one-fresh-member-once-only-mutation',passed:true})
const token='journal:'+d.request.integrationIdentity+':'+hash(d.request.integrationRequest),childKey='physical-epoch-child/v1/'+hash(token)+'/2',child=JSON.parse((await store(join(d.root,'state')))[slot(childKey)]),witnessKey='physical-join/v1/'+child.Invocation
for(const [index,key,change] of [[childKey,x=>({...x,OwnerDigest:'a'.repeat(64)})],[childKey,x=>({...x,State:'active',ExitCode:null,InputClosed:false,OutputDrained:false})],[witnessKey,x=>({...x,InputClosed:false})],[witnessKey,x=>({...x,OutputDrained:false})],[witnessKey,x=>({...x,OwnerPid:0})],[witnessKey,x=>({...x,Args:['read-tree','--reset','-u','changed']})]].map((x,i)=>[i,...x])){
 const storageName='invalid-owner-'+index;await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});const path=join(d.root,storageName,slot(key));await writeFile(path,JSON.stringify(change(JSON.parse(await readFile(path)))));const r=await closed(d,{storageName});assert.ok(!r.events.some(e=>e.operation==='physical-git-start'));assert.deepEqual(r.repositoryBytesBefore,r.repositoryBytesAfter)
}
{const storageName='missing-new-witness';await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});await rm(join(d.root,storageName,slot(witnessKey)));const r=await closed(d,{storageName});assert.ok(!r.events.some(e=>e.operation==='physical-git-start'))}
// Invalid current/proposed identities and authorities never change storage or
// repository bytes. Baseline copies retain their original evidence intact.
const n=await seed('invalid');await closed(n)
for(const value of [undefined,null,0,false,{},[],''])for(const side of ['request','current','both'])await rejected(n,{...n.request,authorization:side==='current'?n.request.authorization:value},{settings:{epochAuthorization:side==='request'?n.request.authorization:value}})
for(const key of ['dispositionAuthorization','continuationAuthorization','interruptedContinuationAuthorization'])for(const value of [null,0,false,{},[],'relabelled'])await rejected(n,n.request,{settings:{[key]:value}})
for(const key of ['id','epoch'])for(const value of [null,0,false,{},[],'','bad id','bad\n','あ','x'.repeat(81),'retain-id','continue-id','retain-continuation-id'])await rejected(n,{...n.request,[key]:value})
for(const key of ['action','scope','predecessorDigest','inventoryDigest','baselineStateDigest','baselineHead','repository','repositoryId','originalRequest','memberIdentity','integrationIdentity','selectedCommit'])await rejected(n,{...n.request,[key]:'changed'})
for(const authority of ['offline-owner-retain','offline-owner-continue','offline-owner-retain-continuation'])await rejected(n,{...n.request,authorization:authority},{settings:{epochAuthorization:authority}})
await rejected(n,{...n.request,memberRequest:line(1,'begin',{target:2,mode:'spawn'}),memberIdentity:hash('1')})
await rejected(n,{...n.request,integrationRequest:line('original-id','integrate',{target:2}),integrationIdentity:hash(JSON.stringify('original-id'))})
for(const value of ['offline-owner-admit ','Offline-owner-admit'])await rejected(n,n.request,{settings:{epochAuthorization:value}})
for(const [index,authorization] of [' ','\t\r\n','権限',' padded ','null'].entries()){
 const storageName='opaque-'+index;await cp(join(n.root,'state'),join(n.root,storageName),{recursive:true});const r=await activate(n,{...n.request,authorization},{storageName,settings:{epochAuthorization:authorization}});assert.ok(r.events.some(e=>e.operation==='outcome'&&e.isOk));assert.deepEqual(r.repositoryBytesBefore,r.repositoryBytesAfter)
}
for(const value of [undefined,'']){const storageName='optional-'+(value===undefined?'omitted':'empty');await cp(join(n.root,'state'),join(n.root,storageName),{recursive:true});const r=await activate(n,n.request,{storageName,settings:{dispositionAuthorization:value,continuationAuthorization:value,interruptedContinuationAuthorization:value,interruptedContinuationStorageContract:value}});assert.ok(r.events.some(e=>e.operation==='outcome'&&e.isOk),JSON.stringify(r.events.slice(-5)));const observed=await run(n,{storageName,settings:{dispositionAuthorization:value,continuationAuthorization:value,interruptedContinuationAuthorization:value}});assert.ok(observed.events.some(e=>e.operation==='read-start'));preserved(n,observed)}
// Rebinding a damaged proposal to its new hashes cannot bypass frozen evidence.
for(const [index,key] of ['registry/v1','physical-continuation-intent/v1','physical-continuation-disposition/v1','registry-member/v1/1'].entries()){
 const storageName='damaged-'+index;await cp(join(n.root,'state'),join(n.root,storageName),{recursive:true});await writeFile(join(n.root,storageName,slot(key)),'{}');const r=await activate(n,n.request,{storageName});untouched(r);assert.ok(r.events.some(e=>e.operation==='outcome'&&!e.isOk));assert.deepEqual(r.repositoryBytesBefore,r.repositoryBytesAfter)
}
for(const key of ['epoch/v1/result','epoch/v1/activation','epoch/v1/blocked','epoch-registry/v1']){
 const storageName='orphan-'+hash(key).slice(0,6);await cp(join(n.root,'state'),join(n.root,storageName),{recursive:true});await writeFile(join(n.root,storageName,slot(key)),'orphan');await rejected(n,n.request,{storageName})
}
{const storageName='orphan-launch';await cp(join(n.root,'state'),join(n.root,storageName),{recursive:true});await writeFile(join(n.root,storageName,slot(childKey)),JSON.stringify(child));const r=await rejected(n,n.request,{storageName});assert.ok(!r.events.some(e=>e.operation==='physical-git-start'));assert.deepEqual(r.repositoryBytesBefore,r.repositoryBytesAfter)}
await admitted(n)
for(const key of ['epoch/v1/blocked','epoch/v1/result','epoch/v1/activation']){
 const storageName='changed-'+hash(key).slice(0,6);await cp(join(n.root,'state'),join(n.root,storageName),{recursive:true});await writeFile(join(n.root,storageName,slot(key)),'changed');await rejected(n,n.request,{storageName});await closed(n,{storageName})
}
for(const key of ['epoch/v1/blocked','epoch/v1/result','epoch/v1/activation']){const storageName='missing-'+hash(key).slice(0,6);await cp(join(n.root,'state'),join(n.root,storageName),{recursive:true});await rm(join(n.root,storageName,slot(key)));if(key!=='epoch/v1/activation')await rejected(n,n.request,{storageName});await closed(n,{storageName})}
await rejected(n,{...n.request,id:'other-admit'});await rejected(n,{...n.request,epoch:'other-epoch'});await rejected(n,JSON.stringify(n.request)+'\n')
for(const x of [d,n]){const r=await run(x,{admin:'continuation',request:x.continuation});assert.deepEqual(r.outputs,['err:unresolved']);untouched(r);preserved(x,r)}
contracts.push({name:'invalid-authority-identity-lineage-and-orphan-refusals',passed:true})
for(const state of ['object-only','ref-only']){const x=await seed('unsupported-'+state,state);await rejected(x,x.request);await closed(x);contracts.push({name:'unsupported-'+state,passed:true})}
for(const damage of ['mode','worktree','index','object','ref']){
 const x=await seed('unowned-'+damage)
 if(damage==='mode')await chmod(join(x.f.root,'file.txt'),0o755)
 if(damage==='worktree')await writeFile(join(x.f.root,'file.txt'),'unowned\n')
 if(damage==='index')git(x.f.root,['read-tree',x.selected])
 if(damage==='object')git(x.f.root,['hash-object','-w','--stdin'],'unowned\n')
 if(damage==='ref')git(x.f.root,['update-ref','refs/heads/unowned',x.selected])
 const r=await rejected(x,x.request);assert.deepEqual(r.repositoryBytesBefore,r.repositoryBytesAfter);contracts.push({name:'unowned-'+damage,passed:true})
}
await rejected(n,n.request,{settings:{evidence:'unavailable'}})
{const x=await seed('before-input');await admitted(x);const r=await run(x,{crash:'before-input'});preserved(x,r);assert.ok(!r.events.some(e=>e.operation==='read-start'||e.operation==='workspaceCreate'));contracts.push({name:'before-input',passed:true})}
for(const window of ['before-fresh-workspace','after-epoch-registry-publication']){
 const x=await seed('member-'+window);await admitted(x);const killed=await run(x,{frames:[x.request.memberRequest],crash:window});preserved(x,killed)
 if(window==='before-fresh-workspace')await closed(x);else{const reopened=await run(x,{frames:[line('inspect-fresh','inspect')]});preserved(x,reopened);assert.ok(!reopened.events.some(e=>e.operation==='provider'||e.operation==='workspaceCreate'))}
 contracts.push({name:'member-'+window,passed:true})
}
// Each explicit administrative publication window can finish its own exact
// records after death. Startup cannot finish any of these windows.
for(const window of ['before-intent','after-intent','before-blocked','after-blocked','before-result','after-result','before-epoch-registry-publication','after-epoch-registry-publication','before-activation','after-activation']){
 const x=await seed('admission-'+window),killed=await activate(x,x.request,{crash:window});preserved(x,killed);assert.deepEqual(killed.repositoryBytesBefore,killed.repositoryBytesAfter)
 if(window!=='after-activation')await closed(x)
 await admitted(x);const replay=await activate(x);untouched(replay);contracts.push({name:'admission-crash-'+window,passed:true})
}
// A fresh interrupted integration stays unknown. Only a retained actual graph
// result can replay; no epoch receipt supplies that application result.
for(const window of ['before-epoch-registry-intent','before-physical-intent','after-physical-intent','after-epoch-registry-intent','after-epoch-journal-intent','before-git-child-start','after-git-object','after-git-ref','after-git-worktree','before-epoch-registry-publication','after-epoch-registry-publication','before-outcome-receipt','after-epoch-journal-completion']){
 const x=await seed('mutation-'+window);await admitted(x);await run(x,{frames:[x.request.memberRequest,line('wait-fresh','wait',{target:2})]});await writeFile(join(x.root,'commit-release'),'release')
 const killed=await run(x,{frames:[x.request.integrationRequest],crash:window});preserved(x,killed)
 const ready=window==='after-epoch-registry-publication'||window==='before-outcome-receipt'||window==='after-epoch-journal-completion'
 if(ready||window==='before-epoch-registry-intent'||window==='before-physical-intent'){
  const again=await run(x,{frames:[x.request.integrationRequest]});preserved(x,again);assert.equal(again.events.filter(e=>e.operation==='physical-git-owner-start').length,window==='before-epoch-registry-intent'||window==='before-physical-intent'?3:0)
 }else await closed(x)
 contracts.push({name:'mutation-crash-'+window,actualResultRequired:true,passed:true})
}
// Custody refusal remains before Git inspection for a retained new no-PID slot
// or an active owned child, even after that child later gains a terminal witness.
for(const command of ['commit-tree','update-ref','read-tree']){
 const x=await seed('active-'+command);await admitted(x);await run(x,{frames:[x.request.memberRequest,line('wait-fresh','wait',{target:2})]});await writeFile(join(x.root,'commit-release'),'release');await s.wrapper(x)
 const killed=await run(x,{frames:[x.request.integrationRequest],held:command}),token='journal:'+x.request.integrationIdentity+':'+hash(x.request.integrationRequest),stage=['commit-tree','update-ref','read-tree'].indexOf(command),child=JSON.parse(killed.after[slot('physical-epoch-child/v1/'+hash(token)+'/'+stage)]),helper=JSON.parse(await readFile(join(x.root,'held.json')))
 try{await closed(x);await writeFile(join(x.root,'child-release'),'release');await until(async()=>!await alive(child.Pid)&&!await alive(helper.pid),'terminal epoch owner');const after=await closed(x);assert.equal(after.after[slot('physical-epoch-child/v1/'+hash(token)+'/'+stage)],killed.after[slot('physical-epoch-child/v1/'+hash(token)+'/'+stage)]);assert.ok(after.after[slot('physical-join/v1/'+child.Invocation)])}
 finally{await writeFile(join(x.root,'child-release'),'release');await until(async()=>!await alive(child.Pid)&&!await alive(helper.pid),'cleanup epoch owner')}
 contracts.push({name:'active-'+command,passed:true,unknownAndFenced:true})
}
await writeFile(join(out,'parent','raw.json'),JSON.stringify(inherited.rows,null,2)+'\n');await writeFile(join(out,'epoch','raw.json'),JSON.stringify(s.rows,null,2)+'\n');await writeFile(join(out,'proof.json'),JSON.stringify({contracts,parentProcesses:inherited.rows.length,epochProcesses:s.rows.length,domains:inherited.domains.length,parentRawHash:hash(JSON.stringify(inherited.rows)),rawHash:hash(JSON.stringify(s.rows))},null,2)+'\n')
console.log('PASS: one explicit graph admission, one fresh Member and once-only physical mutation; old unknowns/fences intact')
