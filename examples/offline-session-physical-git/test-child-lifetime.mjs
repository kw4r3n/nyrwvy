// Controlled offline child lifetime evidence; every Git target is newly provisioned.
import assert from 'node:assert/strict'
import {mkdir,readFile,writeFile,readdir,rm,chmod} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {fixture,inspect,hash} from './git-fixture.mjs'
import {assertMissingActualOutcome} from './outcome-evidence.mjs'
const bundle=resolve(process.argv[2]),out=resolve(process.argv[3]),baseline=process.argv.includes('--baseline'),overflowOnly=process.argv.includes('--overflow-only')
assert.ok(!baseline||!overflowOnly,'baseline and overflow-only are distinct selections')
const command=(id,op,extra={})=>JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:{op,label:op,...extra}}})
const integration=command('same-integration-id','integrate',{target:1}),distinct=command('distinct-id','integrate',{target:1})
const key=k=>hash(k)+'.json',slot=key('physical-child/v1')
const storage=async root=>Object.fromEntries(await Promise.all((await readdir(root)).sort().map(async f=>[f,await readFile(join(root,f),'utf8')])))
const delay=ms=>new Promise(r=>setTimeout(r,ms))
async function until(pred,label){const deadline=Date.now()+20000;while(!await pred()){assert.ok(Date.now()<deadline,'deadline: '+label);await delay(10)}}
async function exists(file){try{await readFile(file);return true}catch{return false}}
const alive=async pid=>{try{return !(await readFile(`/proc/${pid}/stat`,'utf8')).split(') ')[1].startsWith('Z')}catch{return false}}
const counts=r=>Object.fromEntries(['commit-tree','update-ref','read-tree'].map(c=>[c,r.events.filter(e=>e.operation==='physical-git-exit'&&e.command===c&&e.mutation).length]))
const response=(r,id='same-integration-id')=>r.outputs.map(JSON.parse).findLast(x=>x.id===id)?.result?.outcome
const unchanged=r=>{assert.deepEqual(r.before,r.after);assert.deepEqual(r.physicalBefore,r.physicalAfter);assert.deepEqual(counts(r),{'commit-tree':0,'update-ref':0,'read-tree':0});assert.ok(!r.events.some(e=>e.operation==='storage-write-attempt'))}
await mkdir(out,{recursive:true});assert.deepEqual(await readdir(out),[],'fresh child evidence required')
const rows=[],contracts=[]
async function domain(name){const root=join(out,name);await mkdir(join(root,'state'),{recursive:true});return {root,f:await fixture(join(root,'git'))}}
async function run(d,index,{prefix=false,line=integration,crash='',wrapper='',killHeld=!!wrapper,settings={},startupFailure=false,helperMode='hold'}={}){
 const {root,f}=d,state=join(root,'state'),gate=join(root,'commit-release');await rm(gate,{force:true})
 const scenario={kind:'transport',patch:f.patch,providers:{1:'complete'},autoMember:'true',storageDir:state,commitGateFile:gate,journalGateFile:join(root,'journal-release'),crashWindow:crash,snapshotCommit:f.selected,fixtureId:await readFile(join(f.root,'.git','offline-fixture-id'),'utf8'),...settings}
 const path=join(root,`scenario-${index}.json`);await writeFile(path,JSON.stringify(scenario))
 const before=await storage(state),physicalBefore=await inspect(f.root),events=[],outputs=[],actions=[]
 const child=spawn('dotnet',[join(bundle,'Runtime.dll'),path],{stdio:['pipe','pipe','pipe'],env:{...process.env,...(wrapper?{PATH:wrapper+':'+process.env.PATH,NYRWVY_CHILD_ROOT:root,NYRWVY_CHILD_MODE:helperMode}:{} )}})
 let code,signal,failure;const ended=new Promise(r=>child.once('close',(c,s)=>{code=c;signal=s;r()}))
 child.on('error',e=>{failure=e});child.stdin.on('error',e=>{if(code===undefined)failure=e})
 for(const [stream,target] of [[child.stdout,outputs],[child.stderr,events]])createInterface({input:stream}).on('line',line=>{try{target.push(target===events?JSON.parse(line):line)}catch(e){failure=e}})
 const wait=async pred=>until(()=>{if(failure)throw failure;if(code!==undefined&&!pred())throw new Error('early runtime exit: '+JSON.stringify(events.slice(-4)));return pred()},d.root+'/'+index)
 const send=async line=>{const start=outputs.length;actions.push({writeHex:Buffer.from(line+'\n').toString('hex')});child.stdin.write(line+'\n');await wait(()=>outputs.slice(start).some(x=>JSON.parse(x).id===JSON.parse(line).id))}
 const timer=setTimeout(()=>child.kill('SIGKILL'),25000)
 try{
  if(startupFailure){await ended;assert.ok(!events.some(e=>e.operation==='read-start'));assert.equal(events.find(e=>e.operation==='outcome')?.value,'unresolved')}
  else{
   await wait(()=>events.some(e=>e.operation==='read-start'))
   if(prefix){await send(command(1,'begin',{target:1,mode:'spawn'}));await send(command(2,'wait',{target:1}))}
   actions.push({writeHex:Buffer.from(line+'\n').toString('hex')});child.stdin.write(line+'\n');await writeFile(gate,'release\n')
   if(killHeld){await until(()=>exists(join(root,'child-ready.json')),'wrapper held');child.kill('SIGKILL');actions.push({killParentWhileChildHeld:true});await ended;assert.equal(signal,'SIGKILL')}
   else if(crash){await wait(()=>events.some(e=>e.operation==='journal-checkpoint'&&e.phase===crash));child.kill('SIGKILL');actions.push({killAt:crash});await ended;assert.equal(signal,'SIGKILL')}
   else if(wrapper){await wait(()=>events.some(e=>e.operation==='physical-git-termination'));child.stdin.end();await ended;assert.equal(code,0)}
   else{await wait(()=>outputs.some(x=>JSON.parse(x).id===JSON.parse(line).id));child.stdin.end();await ended;assert.equal(code,0)}
  }
  const row={name:root.split('/').at(-1),index,scenario,actions,events,outputs,code,signal,before,after:await storage(state),physicalBefore,physicalAfter:await inspect(f.root)};rows.push(row);await writeFile(join(root,`row-${index}.json`),JSON.stringify(row,null,2)+'\n');return row
 }finally{clearTimeout(timer);if(code===undefined){child.kill('SIGKILL');await ended}}
}
// This controlled helper holds before spawning actual Git. It stays alive after
// its .NET parent dies, then writes one local commit object on explicit release.
// No arbitrary descendant-lifetime claim is inferred from this helper.
async function wrapper(root){
 const bin=join(root,'wrapper');await mkdir(bin)
 const source=`#!/usr/bin/node
import fs from 'node:fs';import {execFileSync} from 'node:child_process';
const args=process.argv.slice(2),root=process.env.NYRWVY_CHILD_ROOT,mode=process.env.NYRWVY_CHILD_MODE;
const input=mode==='stdin-hold'&&args.includes('commit-tree')?Buffer.alloc(0):fs.readFileSync(0);if(args.includes('commit-tree')){
 fs.writeFileSync(root+'/child-ready.json',JSON.stringify({pid:process.pid,args,inputHex:input.toString('hex')}));
 if(mode==='output-limit'||mode==='stderr-output-limit'){const stream=mode==='output-limit'?process.stdout:process.stderr;stream.on('error',()=>{});stream.write('X'.repeat(1048577));}
 while(!fs.existsSync(root+'/child-release'))await new Promise(r=>setTimeout(r,10));
}
const output=execFileSync('/usr/bin/git',args,{input,encoding:'utf8'});
if(args.includes('commit-tree'))fs.writeFileSync(root+'/child-done.json',JSON.stringify({pid:process.pid,output}));
process.stdout.on('error',()=>{});process.stdout.write(output);
`
 await writeFile(join(bin,'git'),source);await chmod(join(bin,'git'),0o755);return bin
}
if(!overflowOnly){
 const d=await domain('surviving-child'),a=await run(d,1,{prefix:true,wrapper:await wrapper(d.root)}),held=JSON.parse(await readFile(join(d.root,'child-ready.json')))
 try{
  assert.ok(await alive(held.pid),'held child survives parent');assert.deepEqual(a.physicalBefore,a.physicalAfter,'held child has not mutated yet')
  const b=await run(d,2,{startupFailure:!baseline}),c=await run(d,3,{line:distinct,startupFailure:!baseline})
  if(baseline){assert.ok(response(b).startsWith('ok:'));assert.equal(counts(b)['commit-tree'],1);contracts.push({name:'surviving-child',negativeControl:true,absenceRetryWhileOriginalChildAlive:true})}
  else{unchanged(b);unchanged(c);assert.equal(JSON.parse(a.after[slot]).State,'active');contracts.push({name:'surviving-child',activeFenceBlocksAdmissionBeforeInput:true})}
  assert.ok(await alive(held.pid),'original child still alive after restart');await writeFile(join(d.root,'child-release'),'release\n');await until(()=>exists(join(d.root,'child-done.json')),'late child completes Git')
  const late=JSON.parse(await readFile(join(d.root,'child-done.json'))),physical=await inspect(d.f.root);assert.ok(physical.objects.includes(late.output.trim()+' commit'))
  if(!baseline){assert.equal(physical.head,d.f.base);assert.equal(physical.worktree,'base\n');const e=await run(d,4,{startupFailure:true});unchanged(e);contracts.at(-1).lateObjectDoesNotClearFence=true}
  await writeFile(join(d.root,'child-observation.json'),JSON.stringify({held,late,physical,parentExited:a.signal,childSurvived:true},null,2)+'\n')
 }finally{await writeFile(join(d.root,'child-release'),'release\n');await until(()=>exists(join(d.root,'child-done.json')),'helper cleanup');await until(async()=>!await alive(held.pid),'helper exit')}
}
if(!baseline){
 for(const mode of overflowOnly?['output-limit','stderr-output-limit']:['hold','stdin-hold','output-limit','stderr-output-limit']){
  const overflow=mode==='output-limit'||mode==='stderr-output-limit'
  const d=await domain('deadline-'+mode),bin=await wrapper(d.root),started=Date.now(),a=await run(d,1,{prefix:true,wrapper:bin,killHeld:false,helperMode:mode,settings:{gitDeadlineMs:'500'}})
  const held=JSON.parse(await readFile(join(d.root,'child-ready.json'))),record=JSON.parse(a.after[slot]);assert.ok(Date.now()-started<15000);assert.ok(!await alive(held.pid),'terminated child joined')
  assert.ok(a.events.some(e=>e.operation==='physical-git-termination'));assert.deepEqual(a.physicalBefore,a.physicalAfter);assert.equal(record.State,overflow?'active':'quiescent')
  if(overflow)assertMissingActualOutcome(a,{wire:true})
  const b=await run(d,2,{startupFailure:overflow});if(overflow)assertMissingActualOutcome(b);else assert.equal(response(b),'err:Git command interrupted; physical effect must be inspected');unchanged(b)
  contracts.push({name:'deadline-'+mode,childTerminatedAndJoined:true,outputDrained:record.OutputDrained,fenceState:record.State,retainedActualFailureReplayed:!overflow,...(overflow?{actualOutcomeAbsent:true,journalPending:true,wireOutcome:'err:unresolved'}:{}),inputBackpressureSaturated:false})
 }
 if(!overflowOnly){
 for(const crash of ['before-git-child-start','after-git-object','after-git-ref','after-git-worktree']){
  const active=crash==='before-git-child-start',d=await domain(crash),a=await run(d,1,{prefix:true,crash}),b=await run(d,2,{startupFailure:active}),c=await run(d,3,{line:distinct,startupFailure:active})
  if(!active){assert.equal(response(b),'err:unresolved');assert.equal(response(c,'distinct-id'),'err:unresolved')}unchanged(b);unchanged(c)
  assert.equal(JSON.parse(a.after[slot]).State,crash==='before-git-child-start'?'active':'quiescent');contracts.push({name:crash,originalOutcomeMissing:true,noReplay:true})
 }
 for(const damage of ['correlation','repository','arguments','input-digest','malformed','legacy-plan','mode','object','ref','restoration','inspection']){
  const d=await domain('negative-'+damage),a=await run(d,1,{prefix:true,crash:'after-git-object'}),file=join(d.root,'state',slot)
  if(['correlation','repository'].includes(damage)){const record=JSON.parse(a.after[slot]);record[damage==='correlation'?'Identity':'RepositoryId']='unowned';await writeFile(file,JSON.stringify(record))}
  if(damage==='arguments'||damage==='input-digest'){const record=JSON.parse(a.after[slot]);if(damage==='arguments')record.Args[1]=d.f.base;else record.InputDigest=hash('unowned');await writeFile(file,JSON.stringify(record))}
  if(damage==='malformed')await writeFile(file,'{broken')
  if(damage==='legacy-plan'){const name=Object.keys(a.after).find(k=>{try{return JSON.parse(a.after[k]).CommitBytes}catch{return false}});const plan=JSON.parse(a.after[name]);plan.Version='2';await writeFile(join(d.root,'state',name),JSON.stringify(plan))}
  if(damage==='mode')await chmod(join(d.f.root,'file.txt'),0o755)
  if(damage==='object'||damage==='ref'){const {git}=await import('./git-fixture.mjs');git(d.f.root,damage==='object'?['hash-object','-w','--stdin']:['update-ref','refs/heads/unowned',d.f.selected],'unowned\n')}
  if(damage==='restoration'){const {git}=await import('./git-fixture.mjs');git(d.f.root,['read-tree','--reset','-u',d.f.selected])}
  const b=await run(d,2,{settings:damage==='inspection'?{evidence:'unavailable'}:{},startupFailure:damage==='malformed'});if(damage!=='malformed')assert.equal(response(b),'err:unresolved');unchanged(b);contracts.push({name:damage,constructedDamage:true,noReplay:true})
 }
 }
}
const proof={baseline,overflowOnly,processes:rows.length,domains:new Set(rows.map(r=>r.name)).size,sigkills:rows.filter(r=>r.signal==='SIGKILL').length,contracts,rawHash:hash(JSON.stringify(rows)),sourceHash:hash(await readFile(new URL(import.meta.url))),outcomeEvidenceHash:hash(await readFile(new URL('./outcome-evidence.mjs',import.meta.url))),limits:['controlled held children, local Git and explicit release; no arbitrary races/uncooperative descendant proof','active launch fence is deliberately not cleared by PID absence or complete physical state; explicit disposition remains missing']}
await writeFile(join(out,'raw.json'),JSON.stringify(rows,null,2)+'\n');await writeFile(join(out,'proof.json'),JSON.stringify(proof,null,2)+'\n')
console.log(`PASS: ${rows.length} serialized child processes, ${proof.sigkills} parent SIGKILLs; ${baseline?'negative baseline demonstrates unsafe retry':'launch fencing and partial/outcome uncertainty'}`)
