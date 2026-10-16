// Separate, explicit retention requests; no live provider or user repository.
import assert from 'node:assert/strict'
import {mkdir,readFile,writeFile,readdir,chmod,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {fixture,inspect,git,hash} from './git-fixture.mjs'
const bundle=resolve(process.argv[2]),out=resolve(process.argv[3]),owner=new URL('./git-owner.mjs',import.meta.url).pathname
const repairOnly=process.argv.includes('--repair-only')
const slot=k=>hash(k)+'.json',childKey=slot('physical-child/v1')
const store=async root=>Object.fromEntries(await Promise.all((await readdir(root)).sort().map(async name=>[name,await readFile(join(root,name),'utf8')])))
const sleep=ms=>new Promise(r=>setTimeout(r,ms)),exists=async p=>{try{await readFile(p);return true}catch{return false}}
async function until(pred,label){const end=Date.now()+25000;while(!await pred()){assert.ok(Date.now()<end,label);await sleep(10)}}
const alive=async pid=>{try{return !(await readFile(`/proc/${pid}/stat`,'utf8')).split(') ')[1].startsWith('Z')}catch{return false}}
const line=(id,op,extra={})=>JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:{op,label:op,...extra}}})
await mkdir(out,{recursive:true});assert.deepEqual(await readdir(out),[],'fresh output required')
const rows=[],contracts=[]
async function domain(name){const root=join(out,name);await mkdir(join(root,'state'),{recursive:true});return {root,f:await fixture(join(root,'git')),seq:0}}
async function run(d,{prefix=false,crash='',held='',request,settings={},startup=false,fault=false,storageName='state',integrationId='original-id'}={}){
 const i=++d.seq,state=join(d.root,storageName),scenario={kind:'transport',patch:d.f.patch,providers:{1:'complete'},autoMember:'true',storageDir:state,commitGateFile:join(d.root,'commit-release'),journalGateFile:join(d.root,'journal-release'),snapshotCommit:d.f.selected,fixtureId:await readFile(join(d.f.root,'.git','offline-fixture-id'),'utf8'),crashWindow:crash,...settings}
 if(request){scenario.dispositionRequest=join(d.root,`request-${i}.json`);scenario.dispositionAuthorization='offline-owner-retain';await writeFile(scenario.dispositionRequest,typeof request==='string'?request:JSON.stringify(request))}
 const path=join(d.root,`scenario-${i}.json`);await writeFile(path,JSON.stringify(scenario))
 const before=await store(state),physicalBefore=await inspect(d.f.root),events=[],outputs=[],actions=[]
 const p=spawn('dotnet',[join(bundle,'Runtime.dll'),path],{stdio:['pipe','pipe','pipe'],env:{...process.env,...(held?{PATH:join(d.root,'bin')+':'+process.env.PATH,NYRWVY_HELD_ROOT:d.root,NYRWVY_HELD_COMMAND:held}:{})}})
 let code,signal,failure;const ended=new Promise(r=>p.once('close',(c,s)=>{code=c;signal=s;r()}));p.on('error',e=>failure=e);p.stdin.on('error',()=>{})
 for(const [stream,target] of [[p.stdout,outputs],[p.stderr,events]])createInterface({input:stream}).on('line',value=>{try{target.push(target===events?JSON.parse(value):value)}catch(e){failure=e}})
 const wait=pred=>until(()=>{if(failure)throw failure;if(code!==undefined&&!pred())throw Error('early exit '+JSON.stringify(events.slice(-3)));return pred()},d.root+'/'+i)
 const send=async input=>{const n=outputs.length;actions.push({writeHex:Buffer.from(input+'\n').toString('hex')});p.stdin.write(input+'\n');await wait(()=>outputs.length>n)}
 const timeout=setTimeout(()=>p.kill('SIGKILL'),30000)
 try{
  if(request||startup){p.stdin.end();await ended;assert.equal(code,0)}
  else{
   await wait(()=>events.some(e=>e.operation==='read-start'))
   if(prefix){await send(line(1,'begin',{target:1,mode:'spawn'}));await send(line(2,'wait',{target:1}))}
   if(fault){await send(line(7,'begin',{target:2,mode:'spawn'}))}
   const input=line(integrationId,'integrate',{target:1});actions.push({writeHex:Buffer.from(input+'\n').toString('hex')});p.stdin.write(input+'\n');
   if(fault){await wait(()=>events.some(e=>e.operation==='step'&&e.arguments?.some(a=>a.parameter==='operation'&&a.value.value==='commit')));p.stdin.write(Buffer.from([255,10]));actions.push({writeHex:'ff0a'});await wait(()=>events.some(e=>e.operation==='provider-ended'&&e.id===2&&e.cancelled))}
   await writeFile(scenario.commitGateFile,'release')
   if(held){await until(()=>exists(join(d.root,'held.json')),'held child');p.kill('SIGKILL');actions.push({killHeld:held});await ended;assert.equal(signal,'SIGKILL')}
   else if(crash){await wait(()=>events.some(e=>e.operation==='journal-checkpoint'&&e.phase===crash));p.kill('SIGKILL');actions.push({killAt:crash});await ended;assert.equal(signal,'SIGKILL')}
   else{if(!fault){await wait(()=>outputs.some(x=>JSON.parse(x).id===integrationId));p.stdin.end()}await ended;assert.equal(code,0)}
  }
  const offered=actions.filter(a=>a.writeHex).map(a=>a.writeHex).join(''),read=events.filter(e=>e.operation==='pipe-read').map(e=>e.hex.toLowerCase()).join('');assert.ok(offered.startsWith(read));if(!request&&!startup&&!fault)assert.equal(read,offered)
  if(!request){const written=events.filter(e=>e.operation==='pipe-write').map(e=>e.hex.toLowerCase()).join('');assert.equal(written,Buffer.from(outputs.map(x=>x+'\n').join('')).toString('hex'))}
  const row={name:d.root.split('/').at(-1),index:i,scenario,request,actions,events,outputs,code,signal,before,after:await store(state),physicalBefore,physicalAfter:await inspect(d.f.root)};rows.push(row);await writeFile(join(d.root,`row-${i}.json`),JSON.stringify(row,null,2)+'\n');return row
 }finally{clearTimeout(timeout);if(code===undefined){p.kill('SIGKILL');await ended}}
}
function untouched(r){assert.deepEqual(r.before,r.after);assert.deepEqual(r.physicalBefore,r.physicalAfter);assert.ok(!r.events.some(e=>e.operation==='storage-write-attempt'||e.operation==='physical-git-exit'&&e.mutation))}
function originalUncertain(r){const c=JSON.parse(r.after[childKey]);assert.equal(r.after[slot('outcome/'+c.Identity)],undefined);assert.equal(r.after[slot('outcome-receipt/'+c.Identity)],undefined);const journal=Object.values(r.after).map(x=>{try{return JSON.parse(x)}catch{return {}}}).find(x=>x.effectIdentity===c.Identity);assert.equal(journal.state,'pending');assert.equal(journal.outcomeSource,'');assert.equal(journal.outcome,'')}
async function request(d,expectedState,id='recovery-id',storageName='state'){
 const map=await store(join(d.root,storageName)),child=JSON.parse(map[childKey]),planKey=slot('physical-plan/v1/'+child.Identity)
 return {id,authorization:'offline-owner-retain',action:'retain-unresolved',invocation:child.Invocation,identity:child.Identity,repository:d.f.root,repositoryId:await readFile(join(d.f.root,'.git','offline-fixture-id'),'utf8'),launchDigest:hash(map[childKey]),planDigest:hash(map[planKey]),expectedState}
}
async function retained(d,q){const a=await run(d,{request:q});assert.deepEqual(a.outputs,['ok:retained-unresolved:'+q.expectedState]);assert.deepEqual(a.physicalBefore,a.physicalAfter);const k=slot('physical-disposition/v1/'+q.id),copy={...a.after};delete copy[k];assert.deepEqual(copy,a.before,'only separate disposition added');assert.equal(JSON.parse(a.after[k]).originalOutcome,'not-inferred');const b=await run(d,{request:q}),c=await run(d,{request:q});untouched(b);untouched(c);assert.deepEqual(b.outputs,a.outputs);assert.deepEqual(c.outputs,a.outputs);return a}
async function reject(d,q,settings={}){const row=await run(d,{request:q,settings});assert.deepEqual(row.outputs,['err:unresolved']);untouched(row);return row}
async function wrapper(d){await mkdir(join(d.root,'bin'));await writeFile(join(d.root,'bin','git'),`#!${process.execPath}
import fs from 'node:fs';import {execFileSync} from 'node:child_process';const args=process.argv.slice(2),root=process.env.NYRWVY_HELD_ROOT,input=fs.readFileSync(0);
if(args.includes(process.env.NYRWVY_HELD_COMMAND)){fs.writeFileSync(root+'/held.json',JSON.stringify({pid:process.pid,args}));while(!fs.existsSync(root+'/child-release'))await new Promise(r=>setTimeout(r,10));}
if(args.includes(process.env.NYRWVY_HELD_COMMAND)&&fs.existsSync(root+'/nonzero'))process.exit(7);
const output=execFileSync('/usr/bin/git',args,{input});process.stdout.write(output);
`);await chmod(join(d.root,'bin','git'),0o755)}
if(!repairOnly){
for(const [command,state] of [['commit-tree','object-only'],['update-ref','ref-only'],['read-tree','applied']]){
 const d=await domain('abandoned-'+command);await wrapper(d);const a=await run(d,{prefix:true,held:command,settings:{gitOwner:owner}});const child=JSON.parse(a.after[childKey]),held=JSON.parse(await readFile(join(d.root,'held.json')))
 try{
  assert.equal(child.State,'active');assert.ok(await alive(held.pid));originalUncertain(a)
  await reject(d,await request(d,command==='commit-tree'?'absent':command==='update-ref'?'object-only':'ref-only'))
  const closed=await run(d,{startup:true});untouched(closed);assert.ok(!closed.events.some(e=>e.operation==='read-start'))
  await writeFile(join(d.root,'child-release'),'release');await until(()=>exists(join(d.root,'state',slot('physical-join/v1/'+child.Invocation))),'durable owner join')
  const witness=JSON.parse(await readFile(join(d.root,'state',slot('physical-join/v1/'+child.Invocation))));assert.equal(witness.LaunchDigest,hash(a.after[childKey]));assert.equal(witness.ExitCode,0);assert.equal(witness.InputClosed,true);assert.equal(witness.OutputDrained,true)
  await until(async()=>!await alive(held.pid)&&!await alive(child.Pid),'owned processes exited')
  const b=await retained(d,await request(d,state));originalUncertain(b);assert.equal(JSON.parse(b.after[childKey]).State,'active')
  for(let n=0;n<2;n++){const r=await run(d,{startup:true});untouched(r);originalUncertain(r);assert.ok(!r.events.some(e=>e.operation==='read-start'))}
  contracts.push({name:d.root.split('/').at(-1),witnessAfterLauncherDeath:true,exactLaunchWitness:true,retainedState:state,originalOutcomeMissing:true,fenceRetained:true,repeatDispositionNoWrite:true,repeatStartupBeforeInput:true,ownerPid:child.Pid,childPid:held.pid})
 }finally{await writeFile(join(d.root,'child-release'),'release');await until(async()=>!await alive(held.pid)&&!await alive(child.Pid),'helper cleanup')}
}
for(const mode of ['nonzero','timeout']){
 const d=await domain('abandoned-absent-'+mode);await wrapper(d);await writeFile(join(d.root,'nonzero'),'exit7');const a=await run(d,{prefix:true,held:'commit-tree',settings:{gitOwner:owner,gitDeadlineMs:mode==='timeout'?'500':'10000'}}),c=JSON.parse(a.after[childKey]),held=JSON.parse(await readFile(join(d.root,'held.json')))
 try{
  if(mode==='nonzero')await writeFile(join(d.root,'child-release'),'release')
  await until(()=>exists(join(d.root,'state',slot('physical-join/v1/'+c.Invocation))),'absent join')
  await until(async()=>!await alive(held.pid)&&!await alive(c.Pid),'absent helper exit')
  const w=JSON.parse(await readFile(join(d.root,'state',slot('physical-join/v1/'+c.Invocation))));if(mode==='nonzero')assert.equal(w.ExitCode,7);else assert.equal(w.ExitSignal,'SIGKILL')
  const b=await retained(d,await request(d,'absent'));originalUncertain(b);assert.equal(JSON.parse(b.after[childKey]).State,'active');const r=await run(d,{startup:true});untouched(r);originalUncertain(r)
  contracts.push({name:'abandoned-absent-'+mode,retainedState:'absent',witnessAfterLauncherDeath:true,originalOutcomeMissing:true,fenceRetained:true,ownerPid:c.Pid,childPid:held.pid})
 }finally{await writeFile(join(d.root,'child-release'),'release');await until(async()=>!await alive(held.pid)&&!await alive(c.Pid),'absent cleanup')}
}
for(const [crash,state] of [['after-git-object','object-only'],['after-git-ref','ref-only'],['after-git-worktree','applied']]){
 const d=await domain('quiescent-'+state);await run(d,{prefix:true,crash});const a=await retained(d,await request(d,state));originalUncertain(a)
 const q=await request(d,state);for(const field of ['authorization','action','invocation','identity','repository','repositoryId','launchDigest','planDigest','expectedState']){await reject(d,{...q,[field]:'changed'})}
 await reject(d,{...q,id:q.identity});await reject(d,q,{evidence:'unavailable'});await reject(d,'{malformed')
 // A valid new identity has its own authorized retention result, no Git effect.
 await retained(d,{...q,id:'separate-recovery-id'});contracts.push({name:'quiescent-'+state,retainedState:state,negativePayloadFields:12,separateIdRetained:true,originalOutcomeMissing:true})
}
for(const damage of ['mode','worktree','index','object','ref','partial-restoration','child-command','side-receipt','old-active','no-pid','missing-witness','false-drain','wrong-witness','unavailable']){
 const d=await domain('negative-'+damage);await run(d,{prefix:true,crash:'after-git-object'});const file=join(d.root,'state',childKey);let c=JSON.parse(await readFile(file))
 if(damage==='mode')await chmod(join(d.f.root,'file.txt'),0o755)
 if(damage==='worktree')await writeFile(join(d.f.root,'file.txt'),'unowned\n')
 if(damage==='index')git(d.f.root,['read-tree',d.f.selected])
 if(damage==='object')git(d.f.root,['hash-object','-w','--stdin'],'unowned\n')
 if(damage==='ref')git(d.f.root,['update-ref','refs/heads/unowned',d.f.selected])
 if(damage==='partial-restoration')git(d.f.root,['read-tree','--reset','-u',d.f.selected])
 if(damage==='child-command'){c.Args[1]=d.f.base;await writeFile(file,JSON.stringify(c))}
 if(damage==='side-receipt')await writeFile(join(d.root,'state',slot('physical-receipt/'+c.Identity+'/commit')),JSON.stringify({phase:'commit',identity:c.Identity,Commit:'unowned'}))
 if(['old-active','no-pid','missing-witness','false-drain','wrong-witness'].includes(damage)){
  c.State='active';c.Version=damage==='old-active'?'1':'2';c.Pid=damage==='no-pid'?null:999999;c.OwnerDigest=hash('controlled');await writeFile(file,JSON.stringify(c))
  if(['false-drain','wrong-witness'].includes(damage)){const w={Version:'1',Invocation:c.Invocation,Identity:c.Identity,Repository:c.Repository,RepositoryId:c.RepositoryId,LaunchDigest:hash(JSON.stringify(c)),OwnerDigest:c.OwnerDigest,OwnerPid:c.Pid,ChildPid:999998,Args:c.Args,InputDigest:c.InputDigest,ExitCode:0,ExitSignal:'',InputClosed:true,OutputDrained:damage!=='false-drain',OutputDigest:hash(''),ErrorDigest:hash('')};if(damage==='wrong-witness')w.Invocation='wrong';await writeFile(join(d.root,'state',slot('physical-join/v1/'+c.Invocation)),JSON.stringify(w))}
 }
 await reject(d,await request(d,'object-only'),damage==='unavailable'?{evidence:'unavailable'}:{});contracts.push({name:damage,rejected:true,preserved:true})
}
for(const crash of ['before-disposition-publication','after-disposition-publication']){
 const d=await domain(crash);await run(d,{prefix:true,crash:'after-git-ref'});const q=await request(d,'ref-only');
 // Run a disposition and kill at its flush boundary, retaining missing response.
 const requestPath=join(d.root,'crash-request.json');await writeFile(requestPath,JSON.stringify(q));const scenarioPath=join(d.root,'crash-scenario.json');const scenario={storageDir:join(d.root,'state'),fixtureId:q.repositoryId,dispositionRequest:requestPath,dispositionAuthorization:q.authorization,crashWindow:crash,journalGateFile:join(d.root,'never-release')};await writeFile(scenarioPath,JSON.stringify(scenario));
 const before=await store(scenario.storageDir),physicalBefore=await inspect(d.f.root),events=[],outputs=[];const p=spawn('dotnet',[join(bundle,'Runtime.dll'),scenarioPath]);const ended=new Promise(r=>p.once('close',(code,signal)=>r({code,signal})));createInterface({input:p.stderr}).on('line',x=>events.push(JSON.parse(x)));createInterface({input:p.stdout}).on('line',x=>outputs.push(x));await until(()=>events.some(e=>e.operation==='journal-checkpoint'&&e.phase===crash),'disposition checkpoint');p.kill('SIGKILL');const exit=await ended;assert.equal(exit.signal,'SIGKILL');
 const row={name:crash,index:++d.seq,scenario,request:q,actions:[{killAt:crash}],events,outputs,...exit,before,after:await store(scenario.storageDir),physicalBefore,physicalAfter:await inspect(d.f.root)};rows.push(row);await writeFile(join(d.root,`row-${d.seq}.json`),JSON.stringify(row,null,2)+'\n');assert.deepEqual(physicalBefore,row.physicalAfter);assert.deepEqual(outputs,[])
 if(crash==='before-disposition-publication'){untouched(row);await retained(d,q)}else{const r=await run(d,{request:q});untouched(r);assert.deepEqual(r.outputs,['ok:retained-unresolved:ref-only'])}
 contracts.push({name:crash,separatePublicationCrash:true,originalPreserved:true,exactRetry:true})
}
const d=await domain('actual-outcome-preserved'),actual=await run(d,{prefix:true});const c=JSON.parse(actual.after[childKey]);assert.ok(actual.after[slot('outcome/'+c.Identity)]);await retained(d,await request(d,'applied'));contracts.push({name:'actual-outcome-preserved',originalActualBytesPreserved:true})
const cancelled=await domain('actual-cancellation-preserved'),a=await run(cancelled,{prefix:true,fault:true,settings:{providers:{1:'complete',2:'hold'}}}),cc=JSON.parse(a.after[childKey]);assert.equal(a.after[slot('outcome/'+cc.Identity)],'err:cancelled');await retained(cancelled,await request(cancelled,'applied'));contracts.push({name:'actual-cancellation-preserved',originalActualBytesPreserved:true,exactCancellation:true})
const prelaunch=await domain('owner-prelaunch-ambiguity');const pre=await run(prelaunch,{prefix:true,crash:'before-git-child-start',settings:{gitOwner:owner}});assert.equal(JSON.parse(pre.after[childKey]).Pid,null);await reject(prelaunch,await request(prelaunch,'absent'));contracts.push({name:'owner-prelaunch-ambiguity',rejected:true,originalPreserved:true})
const contradictions=await domain('disposition-record-contradictions');await run(contradictions,{prefix:true,crash:'after-git-ref'});const rq=await request(contradictions,'ref-only');await retained(contradictions,rq);const df=join(contradictions.root,'state',slot('physical-disposition/v1/'+rq.id)),originalRecord=await readFile(df,'utf8')
for(const field of ['version','id','requestDigest','invocation','identity','launchDigest','planDigest','repository','repositoryId','action','state','witnessDigest','stateDigest','result','originalOutcome']){const copy=JSON.parse(originalRecord);copy[field]='contradiction';await writeFile(df,JSON.stringify(copy));await reject(contradictions,rq)}
// Restore only the separate constructed recovery-record control, preserving all
// raw contradictory rows. No original journal/launch/plan/outcome is rewritten.
await writeFile(df,originalRecord);await reject(contradictions,rq,{worktreeModeControl:'content-only'});await reject(contradictions,rq,{physicalControl:'receipt-absence'});contracts.push({name:'disposition-record-contradictions',constructedCopies:15,rejected:true,originalApplicationBytesPreserved:true,unsupportedControlsRejected:true})
}
// Review repair: use ordinary Git, not wrappers that consume otherwise unused stdin.
for(const cancelled of [false,true]){
 const d=await domain(cancelled?'repair-ordinary-cancelled':'repair-ordinary-success'),a=await run(d,{prefix:true,fault:cancelled,settings:{gitOwner:owner,...(cancelled?{providers:{1:'complete',2:'hold'}}:{})}}),c=JSON.parse(a.after[childKey])
 const actual=a.after[slot('outcome/'+c.Identity)];assert.ok(cancelled?actual==='err:cancelled':actual.startsWith('ok:'));assert.equal(a.after[slot('outcome-receipt/'+c.Identity)],actual)
 const journal=Object.values(a.after).map(x=>{try{return JSON.parse(x)}catch{return {}}}).find(x=>x.effectIdentity===c.Identity);assert.equal(journal.state,'done');assert.equal(journal.outcomeSource,'actual');assert.equal(journal.outcome,actual)
 const starts=a.events.filter(e=>e.operation==='physical-git-owner-start');assert.equal(starts.length,3);assert.equal(a.events.filter(e=>e.operation==='physical-child-quiescent'&&e.owner).length,3)
 for(const start of starts){const w=JSON.parse(a.after[slot('physical-join/v1/'+start.Invocation)]);assert.equal(w.OwnerPid,start.pid);assert.equal(w.ExitCode,0);assert.equal(w.InputClosed,true);assert.equal(w.OutputDrained,true);assert.equal(w.OwnerDigest,hash(await readFile(owner)));if(start.command!=='commit-tree')assert.equal(w.InputDigest,hash(''))}
 const b=await run(d,{settings:{gitOwner:owner}});untouched(b);assert.equal(b.outputs.map(JSON.parse).find(x=>x.id==='original-id').result.outcome,actual)
 await retained(d,await request(d,'applied'));const replay=await run(d,{settings:{gitOwner:owner}});untouched(replay);assert.equal(replay.outputs.map(JSON.parse).find(x=>x.id==='original-id').result.outcome,actual)
 const distinct=await run(d,{integrationId:'new-ordinary-id',settings:{gitOwner:owner}});assert.deepEqual(distinct.physicalBefore,distinct.physicalAfter);assert.ok(!distinct.events.some(e=>e.operation==='physical-git-owner-start'));assert.ok(distinct.outputs.map(JSON.parse).find(x=>x.id==='new-ordinary-id').result.outcome.startsWith('ok:'))
 contracts.push({name:d.root.split('/').at(-1),ordinaryForeground:true,mutationJoins:3,originalActualBytesPreserved:true,exactCancellation:cancelled,sameIdReplay:true,distinctIdNoMutation:true})
}
const {cp}=await import('node:fs/promises')
async function copyRequest(d,label,state,{id=label,change=async()=>{},accept=false}={}){
 const storageName='copy-'+label;await cp(join(d.root,'state'),join(d.root,storageName),{recursive:true});const q=await request(d,state,id,storageName);await change(join(d.root,storageName),q)
 const row=await run(d,{request:q,storageName});assert.deepEqual(row.outputs,[accept?'ok:retained-unresolved:'+state:'err:unresolved']);assert.deepEqual(row.physicalBefore,row.physicalAfter)
 if(accept){const k=slot('physical-disposition/v1/'+id),copy={...row.after};delete copy[k];assert.deepEqual(copy,row.before);const replay=await run(d,{request:q,storageName});untouched(replay);assert.deepEqual(replay.outputs,row.outputs)}else untouched(row)
 return row
}
for(const [label,state,crash] of [['object','object-only','after-git-object'],['ref','ref-only','after-git-ref'],['applied','applied','after-git-worktree'],['active-object','object-only',''],['absent','absent','']]){
 const d=await domain('repair-tail-'+label)
 if(crash)await run(d,{prefix:true,crash});else{
  await wrapper(d);if(label==='absent')await writeFile(join(d.root,'nonzero'),'exit7');const a=await run(d,{prefix:true,held:'commit-tree',settings:{gitOwner:owner}}),c=JSON.parse(a.after[childKey]),held=JSON.parse(await readFile(join(d.root,'held.json')))
  try{await writeFile(join(d.root,'child-release'),'release');await until(()=>exists(join(d.root,'state',slot('physical-join/v1/'+c.Invocation))),'repair join');await until(async()=>!await alive(c.Pid)&&!await alive(held.pid),'repair helper exit')}
  finally{await writeFile(join(d.root,'child-release'),'release');await until(async()=>!await alive(c.Pid)&&!await alive(held.pid),'repair cleanup')}
 }
 const seed=await store(join(d.root,'state')),c=JSON.parse(seed[childKey]),plan=JSON.parse(seed[slot('physical-plan/v1/'+c.Identity)])
 const missing=await copyRequest(d,'missing-receipts',state,{accept:true,change:async path=>{for(const phase of ['commit','head','save'])await rm(join(path,slot('physical-receipt/'+c.Identity+'/'+phase)),{force:true})}})
 const retentionTemplate=JSON.parse(missing.after[slot('physical-disposition/v1/missing-receipts')])
 for(const phase of ['commit','head','save']){
  const accept=state==='applied'||state==='ref-only'&&phase!=='save'
  const receipt=JSON.stringify({phase,identity:c.Identity,Commit:plan.Commit})
  await copyRequest(d,'phase-'+phase,state,{accept,change:async path=>{await writeFile(join(path,slot('physical-receipt/'+c.Identity+'/'+phase)),receipt)}})
  if(!accept)await copyRequest(d,'retained-phase-'+phase,state,{change:async(path,q)=>{await writeFile(join(path,slot('physical-receipt/'+c.Identity+'/'+phase)),receipt);await writeFile(join(path,slot('physical-disposition/v1/'+q.id)),JSON.stringify({...retentionTemplate,id:q.id,requestDigest:hash(JSON.stringify(q))}))}})
 }
 if(crash&&state!=='object-only')await copyRequest(d,'latest-commit-tree',state,{change:async(path,q)=>{const copy={...c,Args:['commit-tree',plan.Tree,'-p',plan.Before.Head],InputDigest:hash(c.Identity+'\n')},raw=JSON.stringify(copy);await writeFile(join(path,childKey),raw);q.launchDigest=hash(raw)}})
 if(state==='object-only'&&crash){
  await copyRequest(d,'latest-read-tree',state,{change:async(path,q)=>{const raw=JSON.stringify({...c,Args:['read-tree','--reset','-u',plan.Commit],InputDigest:hash('')});await writeFile(join(path,childKey),raw);q.launchDigest=hash(raw)}})
  const invalid=['','a'.repeat(81),'abc\n','abc\r\n','\nabc','a\nb','a b','a\tb','a\0b','a/b','日本語','Ａ','abc\r',null,0]
  for(const [i,id] of invalid.entries())await copyRequest(d,'invalid-id-'+i,state,{id})
  for(const [i,id] of ['a','A1_-','_','-','a'.repeat(80)].entries())await copyRequest(d,'valid-id-'+i,state,{id,accept:true})
 }
 if(label==='active-object'){
  for(const field of ['OutputDigest','ErrorDigest'])for(const suffix of ['\n','\r\n'])await copyRequest(d,'witness-'+field+'-'+(suffix.length===1?'lf':'crlf'),state,{change:async path=>{const file=join(path,slot('physical-join/v1/'+c.Invocation)),w=JSON.parse(await readFile(file));w[field]+=suffix;await writeFile(file,JSON.stringify(w))}})
  await copyRequest(d,'retained-active',state,{accept:true});const r=await run(d,{startup:true,storageName:'copy-retained-active'});untouched(r);originalUncertain(r);assert.ok(!r.events.some(e=>e.operation==='read-start'))
 }
 assert.deepEqual(await store(join(d.root,'state')),seed,'repair seed originals preserved');contracts.push({name:d.root.split('/').at(-1),state,receiptPhaseAndLatestInvocation:true,missingReceiptsNonAuthoritative:true,seedOriginalsPreserved:true})
}
const proof={repairOnly,processes:rows.length,domains:new Set(rows.map(r=>r.name)).size,sigkills:rows.filter(r=>r.signal==='SIGKILL').length,contracts,rawHash:hash(JSON.stringify(rows)),sourceHash:hash(await readFile(new URL(import.meta.url))),ownerHash:hash(await readFile(owner)),limits:['one trusted serialized writer and intact flushed storage','constructed storage copies retain raw snapshots; not historical authentication','controlled Linux children only; no escaped descendants or historical authentication','retain-unresolved only; no physical tail continuation or original outcome reconstruction']}
await writeFile(join(out,'raw.json'),JSON.stringify(rows,null,2)+'\n');await writeFile(join(out,'proof.json'),JSON.stringify(proof,null,2)+'\n')
console.log(`PASS: ${proof.processes} disposition/application processes, ${proof.domains} disposable domains, ${proof.sigkills} SIGKILLs`)
