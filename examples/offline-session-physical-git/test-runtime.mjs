import assert from 'node:assert/strict'
import {readFile,writeFile,mkdir,readdir,rm,access} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {fixture,git,inspect,hash} from './git-fixture.mjs'
const dir=resolve(process.argv[2]),rows=[],proof=[]
const command=(id,op,label,extra={})=>JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:{op,label,...extra}}})
const integration=command('same-integration-id','integrate','held',{target:1}),distinct=command('new-integration-id','integrate','held',{target:1})
const recordKey='journal/v1/'+hash(JSON.stringify('same-integration-id')),token='journal:'+hash(JSON.stringify('same-integration-id'))+':'+hash(integration)
const key=k=>hash(k)+'.json',arg=(e,k)=>e.arguments?.find(x=>x.parameter===k)?.value.value
const storage=async root=>Object.fromEntries(await Promise.all((await readdir(root)).sort().map(async f=>[f,await readFile(join(root,f),'utf8')])))
const physicalCounts=r=>Object.fromEntries(['commit-tree','update-ref','read-tree'].map(c=>[c,r.events.filter(e=>e.operation==='physical-git-exit'&&e.command===c&&e.mutation).length]))
const total=rs=>Object.fromEntries(['commit-tree','update-ref','read-tree'].map(c=>[c,rs.reduce((n,r)=>n+physicalCounts(r)[c],0)]))
const noEffects=r=>assert.deepEqual(physicalCounts(r),{'commit-tree':0,'update-ref':0,'read-tree':0})
const noProvider=r=>assert.ok(!r.events.some(e=>['provider','workspaceCreate','snapshot'].includes(e.operation)))
const response=(r,id='same-integration-id')=>r.outputs.map(JSON.parse).findLast(x=>x.id===id)?.result
const outcome=(r,id)=>response(r,id)?.outcome
const publication=r=>JSON.parse(r.storageAfter[key('registry/v1')])
const inspectReply=r=>r.events.findLast(e=>e.operation==='reply'&&arg(e,'label')==='restored')&&arg(r.events.findLast(e=>e.operation==='reply'&&arg(e,'label')==='restored'),'record')
const plan=r=>JSON.parse(r.storageAfter[key('physical-plan/v1/'+token)])
const unchanged=r=>{noEffects(r);assert.deepEqual(r.storageBefore,r.storageAfter);assert.deepEqual(r.physicalBefore,r.physicalAfter)}
async function execute(name,index,{crash='',evidence='',line=integration,fault='',lost=false,witness=false,withPrefix=index===1,inspectMember=false,startupFailure=false,physicalControl='',restorationDisabled=false,customCommands,providerKinds}={}){
 const root=join(dir,'runs',name),state=join(root,'state');await mkdir(state,{recursive:true})
 let f;try{f=JSON.parse(await readFile(join(root,'fixture.json'),'utf8'))}catch{f=await fixture(join(root,'git'));await writeFile(join(root,'fixture.json'),JSON.stringify(f))}
 const gate=join(root,'commit-release'),journalGate=join(root,'journal-release');await rm(gate,{force:true});await rm(journalGate,{force:true})
 const scenario={kind:'transport',patch:f.patch,providers:providerKinds??{1:'complete',2:witness?'hold':'complete',3:'complete'},autoMember:'true',storageDir:state,commitGateFile:gate,journalGateFile:journalGate,crashWindow:crash,evidence,snapshotCommit:f.selected,fixtureId:await readFile(join(f.root,'.git','offline-fixture-id'),'utf8'),physicalControl}
 const scenarioFile=join(root,`scenario-${index}.json`);await writeFile(scenarioFile,JSON.stringify(scenario))
 const storageBefore=await storage(state),physicalBefore=await inspect(f.root)
 const child=spawn('dotnet',[join(dir,...(restorationDisabled?['restoration-disabled','Runtime.dll']:['Runtime.dll'])),scenarioFile],{stdio:['pipe','pipe','pipe']})
 const events=[],outputs=[],actions=[],waiters=new Set();let failure,exitCode,signal
 const wake=()=>{for(const w of waiters)w()}
 for(const [stream,target] of [[child.stdout,outputs],[child.stderr,events]])createInterface({input:stream}).on('line',line=>{try{target.push(target===events?JSON.parse(line):line)}catch(e){failure=e}wake()})
 child.on('error',e=>{failure=e;wake()});child.stdin.on('error',e=>{if(exitCode===undefined)failure=e;wake()})
 const ended=new Promise(resolve=>child.once('close',(code,sig)=>{exitCode=code;signal=sig;wake();resolve()}))
 const timer=setTimeout(()=>{failure=new Error(`physical deadline: ${name}/${index}`);child.kill('SIGKILL');wake()},30000)
 const until=async pred=>{if(pred())return;await new Promise((resolve,reject)=>{const check=()=>{if(pred()){waiters.delete(check);resolve()}else if(failure||exitCode!==undefined){waiters.delete(check);reject(failure??new Error('early exit '+name+': '+JSON.stringify(events.slice(-5))))}};waiters.add(check);check()})}
 const write=async bytes=>{actions.push({writeHex:bytes.toString('hex')});await new Promise((resolve,reject)=>child.stdin.write(bytes,e=>e?reject(e):resolve()))}
 const send=async line=>{const offset=outputs.length;await write(Buffer.from(line+'\n'));await until(()=>outputs.slice(offset).some(x=>JSON.parse(x).id===JSON.parse(line).id))}
 try{
  if(startupFailure){await ended;assert.equal(events.find(e=>e.operation==='outcome')?.value,'unresolved');assert.ok(!events.some(e=>e.operation==='read-start'));assert.equal(outputs.length,0)}
  else{
   await until(()=>events.some(e=>e.operation==='read-start'))
   if(withPrefix){await send(command(1,'begin','member',{target:1,mode:'spawn'}));await until(()=>events.some(e=>e.operation==='gate'&&arg(e,'label')==='released:member'||e.operation==='storage-read'&&e.key==='registry-admission/v1/'+hash('1')&&e.present));await send(command(2,'wait','settled',{target:1}))}
   if(inspectMember)await send(command(10,'inspect','restored',{target:1}))
   if(customCommands){for(const line of customCommands)await send(line);child.stdin.end();actions.push({eof:true});await ended}
   else{
    if(witness){await send(command(7,'begin','witness',{target:2,mode:'spawn'}));await until(()=>events.some(e=>e.operation==='provider'&&arg(e,'id')==='2'))}
    await write(Buffer.from(line+'\n'))
    if(lost||fault){await until(()=>events.some(e=>e.operation==='step'&&arg(e,'operation')==='commit'))
     if(lost){child.stdout.destroy();actions.push({closePeerOutput:true})}
     else{await write(fault==='oversize'?Buffer.alloc(16385,65):Buffer.from([255,10]));await until(()=>events.some(e=>e.operation==='provider-ended'&&e.id===2&&e.cancelled));await new Promise(r=>setTimeout(r,350));assert.ok(!events.some(e=>e.operation==='physical-git-exit'&&e.mutation));actions.push({heldObservation:{ms:350,mutation:false,witnessCancelled:true}})}
    }
    await writeFile(gate,'release\n');actions.push({releaseCommit:true})
    if(crash){await until(()=>events.some(e=>e.operation==='journal-checkpoint'&&e.phase===crash));assert.ok(events.filter(e=>e.operation==='physical-git-exit').every(e=>e.childExited));child.kill('SIGKILL');actions.push({signal:'SIGKILL',afterCheckpoint:crash});await ended;assert.equal(signal,'SIGKILL');assert.equal(exitCode,null)}
    else{if(!lost&&!fault){await until(()=>outputs.some(x=>JSON.parse(x).id===JSON.parse(line).id));child.stdin.end();actions.push({eof:true})}await ended}
   }
  }
  if(failure)throw failure
  if(!crash){const result=events.find(e=>e.operation==='outcome');assert.ok(result);assert.equal(exitCode,0);assert.equal(signal,null);assert.equal(result.Opened,1);assert.equal(result.Closed,1);assert.equal(result.Active,0)
   assert.equal(events.filter(e=>e.operation==='input-fd-closed').length,1);assert.equal(events.filter(e=>e.operation==='transport-close').length,1)
   if(fault){assert.equal(result.ok,false);assert.equal(result.type,fault==='oversize'?'InvalidDataException':'DecoderFallbackException');assert.equal(result.readerFaultIdentity,true);assert.equal(result.readerFaultStack,true)}
   else if(lost){assert.equal(result.ok,false);assert.equal(result.type,'IOException');assert.equal(result.OutputFault,'IOException');assert.equal(result.TerminalWritten,false);assert.equal(outcome({outputs}),undefined)}
   else if(!startupFailure){assert.equal(result.ok,true);assert.equal(result.isOk,true)}
  }
  assert.ok(events.filter(e=>e.operation==='protected-step'||e.operation==='storage-written'&&e.key.startsWith('outcome')).every(e=>e.cancelled===false))
  const offered=actions.filter(a=>a.writeHex).map(a=>a.writeHex).join(''),read=events.filter(e=>e.operation==='pipe-read').map(e=>e.hex.toLowerCase()).join('')
  assert.ok(offered.startsWith(read));if(!fault)assert.equal(read,offered)
  const written=events.filter(e=>e.operation==='pipe-write').map(e=>e.hex.toLowerCase()).join('')
  assert.equal(written,Buffer.from(outputs.map(x=>x+'\n').join('')).toString('hex'))
  const row={name,index,crash,fault,lost,physicalControl,restorationDisabled,fixture:f,scenario,events,outputs,actions,exitCode,signal,storageBefore,storageAfter:await storage(state),physicalBefore,physicalAfter:await inspect(f.root)};rows.push(row)
  await writeFile(join(root,`row-${index}.json`),JSON.stringify(row,null,2)+'\n');return row
 }finally{clearTimeout(timer);if(exitCode===undefined){child.kill('SIGKILL');await ended}}
}
await mkdir(join(dir,'runs'),{recursive:true});assert.deepEqual(await readdir(join(dir,'runs')),[],'fresh runtime domains required')
{
 const a=await execute('restoration',1),b=await execute('restoration',2,{inspectMember:true}),c=await execute('restoration',3,{line:distinct,inspectMember:true})
 const commit=plan(a).Commit;assert.equal(outcome(a),'ok:'+commit);assert.equal(outcome(b),'ok:'+commit);assert.equal(outcome(c,'new-integration-id'),'ok:'+commit)
 unchanged(b);noEffects(c);assert.deepEqual(c.physicalBefore,c.physicalAfter);noProvider(b);noProvider(c);assert.ok(inspectReply(b).includes(commit));assert.ok(inspectReply(c).includes(commit))
 assert.deepEqual(total([a,b,c]),{'commit-tree':1,'update-ref':1,'read-tree':1})
 proof.push({name:'restoration',processes:3,total:total([a,b,c]),headIsReadOnly:true})
}
{
 const a=await execute('lost-response',1,{lost:true}),b=await execute('lost-response',2),c=await execute('lost-response',3,{line:distinct})
 assert.equal(outcome(b),'ok:'+plan(a).Commit);assert.equal(outcome(c,'new-integration-id'),'ok:'+plan(a).Commit);unchanged(b);noEffects(c);assert.deepEqual(c.physicalBefore,c.physicalAfter);noProvider(b);noProvider(c)
 assert.deepEqual(total([a,b,c]),{'commit-tree':1,'update-ref':1,'read-tree':1});proof.push({name:'lost-response',processes:3,total:total([a,b,c]),actualOutcomeRecovered:true})
}
for(const crash of ['after-physical-intent','after-registry-intent','after-intent','after-git-object','after-git-ref','after-commit','after-git-worktree','before-registry-publication','after-registry-publication','before-outcome-receipt','after-effect']){
 const a=await execute(crash,1,{crash}),b=await execute(crash,2,{line:crash==='after-physical-intent'?integration:distinct}),c=await execute(crash,3,{line:crash==='after-physical-intent'?distinct:integration})
 const absent=['after-physical-intent','after-registry-intent','after-intent'].includes(crash),published=['after-registry-publication','before-outcome-receipt','after-effect'].includes(crash)
 if(crash==='after-physical-intent'){assert.equal(outcome(b),'ok:'+plan(b).Commit);assert.equal(outcome(c,'new-integration-id'),'ok:'+plan(c).Commit);noEffects(c)}
 else if(absent){assert.equal(outcome(b,'new-integration-id'),'err:unresolved');unchanged(b);assert.equal(outcome(c),'ok:'+plan(c).Commit)}
 else if(published){assert.equal(outcome(b,'new-integration-id'),'ok:'+plan(a).Commit);assert.equal(outcome(c),'ok:'+plan(a).Commit);noEffects(b);noEffects(c);assert.deepEqual(c.physicalBefore,c.physicalAfter)}
 else{assert.equal(outcome(b,'new-integration-id'),'err:unresolved');assert.equal(outcome(c),'err:unresolved');unchanged(b);unchanged(c)}
 if(crash!=='after-physical-intent'){noProvider(b);noProvider(c)}
 if(crash==='after-git-object'){assert.equal(a.physicalAfter.head,a.fixture.base);assert.notDeepEqual(a.physicalAfter.objects,a.physicalBefore.objects);assert.equal(a.storageAfter[key('physical-receipt/'+token+'/commit')],undefined)}
 if(['after-git-worktree','before-registry-publication'].includes(crash)){assert.equal(a.physicalAfter.head,plan(a).Commit);assert.equal(a.physicalAfter.worktree,'selected\n');assert.equal(outcome(c),'err:unresolved')}
 proof.push({name:crash,processes:3,sigkill:true,absent,published,newId:outcome(b,'new-integration-id'),sameId:outcome(c),total:total([a,b,c])})
}
for(const crash of ['before-registry-publication','after-registry-publication','before-outcome-receipt','after-effect']){
 const name='cancel-'+crash,a=await execute(name,1,{crash,fault:'utf8',witness:true}),b=await execute(name,2),c=await execute(name,3,{line:distinct})
 assert.equal(JSON.parse(a.events.find(e=>e.operation==='storage-write-attempt'&&e.key==='registry/v1'&&JSON.parse(e.value).phase==='ready').value).outcome,'err:cancelled')
 const expected=crash==='before-registry-publication'?'err:unresolved':'err:cancelled';assert.equal(outcome(b),expected);assert.equal(outcome(c,'new-integration-id'),expected==='err:unresolved'?expected:'ok:'+plan(a).Commit)
 noEffects(b);noEffects(c);noProvider(b);noProvider(c);if(expected==='err:unresolved'){unchanged(b);unchanged(c)}
 assert.deepEqual(total([a,b,c]),{'commit-tree':1,'update-ref':1,'read-tree':1});proof.push({name,processes:3,sigkill:true,exactActualCancellation:expected==='err:cancelled',sameId:outcome(b),newId:outcome(c,'new-integration-id'),total:total([a,b,c])})
}
for(const fault of ['utf8','oversize']){
 const name='reader-'+fault,a=await execute(name,1,{fault,witness:true}),b=await execute(name,2),c=await execute(name,3,{line:distinct})
 assert.equal(outcome(b),'err:cancelled');assert.equal(outcome(c,'new-integration-id'),'ok:'+plan(a).Commit);unchanged(b);noEffects(c);assert.deepEqual(c.physicalBefore,c.physicalAfter);noProvider(b);noProvider(c)
 proof.push({name,processes:3,exactReaderException:true,exactCancellation:true,total:total([a,b,c])})
}
{
 const a=await execute('unavailable',1,{crash:'after-intent'}),b=await execute('unavailable',2,{evidence:'unavailable'}),c=await execute('unavailable',3)
 assert.equal(outcome(b),'err:unresolved');unchanged(b);assert.equal(outcome(c),'ok:'+plan(a).Commit);proof.push({name:'unavailable',processes:3,unknownNeverAutomaticallyRetries:true})
}
{
 const a=await execute('payload-conflict',1,{crash:'after-intent'}),b=await execute('payload-conflict',2,{line:command('same-integration-id','integrate','different',{target:1})})
 assert.equal(outcome(b),'err:payload-conflict');assert.ok(!b.events.some(e=>e.operation==='effect-evidence'));unchanged(b);proof.push({name:'payload-conflict',processes:2,beforeInspection:true})
}
{
 const a=await execute('selected-drift',1,{crash:'after-intent'}),v=publication(a);v.entries[0].commit=a.fixture.base;await writeFile(join(dir,'runs','selected-drift','state',key('registry/v1')),JSON.stringify(v))
 const b=await execute('selected-drift',2);assert.equal(outcome(b),'err:unresolved');unchanged(b);proof.push({name:'selected-drift',processes:2,authoritativeAbsenceDoesNotOverrideChangedIntent:true})
}
for(const damage of ['unreachable-object','index-worktree','extra-ref','missing-plan','contradictory-receipt','unowned-plan','repository-identity']){
 const name='pending-'+damage,a=await execute(name,1,{crash:'after-intent'}),state=join(dir,'runs',name,'state')
 if(damage==='unreachable-object')git(a.fixture.root,['hash-object','-w','--stdin'],'unowned\n')
 if(damage==='index-worktree')git(a.fixture.root,['read-tree','--reset','-u',a.fixture.selected])
 if(damage==='extra-ref')git(a.fixture.root,['update-ref','refs/heads/unowned',a.fixture.selected])
 if(damage==='missing-plan')await rm(join(state,key('physical-plan/v1/'+token)))
 if(damage==='contradictory-receipt')await writeFile(join(state,key('physical-receipt/'+token+'/commit')),JSON.stringify({phase:'commit',identity:'unowned',Commit:a.fixture.base}))
 if(damage==='unowned-plan'){const p=plan(a);p.Identity='unowned';await writeFile(join(state,key('physical-plan/v1/'+token)),JSON.stringify(p))}
 if(damage==='repository-identity'){const p=plan(a);p.RepositoryId='unowned';await writeFile(join(state,key('physical-plan/v1/'+token)),JSON.stringify(p))}
 const b=await execute(name,2),c=await execute(name,3);assert.equal(outcome(b),'err:unresolved');assert.equal(outcome(c),'err:unresolved');unchanged(b);unchanged(c)
 assert.ok(b.events.some(e=>e.operation==='effect-evidence'&&e.state==='unknown'));proof.push({name,processes:3,constructedDamage:true,unknownNeverAutomaticallyRetries:true})
}
for(const damage of ['unreachable-object','head-ref','index-worktree','missing-plan','receipt','actual-outcome']){
 const name='ready-'+damage,a=await execute(name,1),state=join(dir,'runs',name,'state')
 if(damage==='unreachable-object')git(a.fixture.root,['hash-object','-w','--stdin'],'unowned\n')
 if(damage==='head-ref')git(a.fixture.root,['update-ref','refs/heads/main',a.fixture.base])
 if(damage==='index-worktree')git(a.fixture.root,['read-tree','--reset','-u',a.fixture.base])
 if(damage==='missing-plan')await rm(join(state,key('physical-plan/v1/'+token)))
 if(damage==='receipt')await writeFile(join(state,key('physical-receipt/'+token+'/save')),'{broken')
 if(damage==='actual-outcome')await writeFile(join(state,key('outcome/'+token)),'err:contradiction')
 const b=await execute(name,2,{startupFailure:true});unchanged(b);noProvider(b);assert.ok(!b.events.some(e=>e.operation==='storage-write-attempt'));proof.push({name,processes:2,constructedDamage:true,closedBeforeReaderAndRepair:true})
}
{
 const a=await execute('receipt-only-control',1,{crash:'after-git-object',physicalControl:'receipt-absence'}),b=await execute('receipt-only-control',2,{physicalControl:'receipt-absence'})
 assert.equal(outcome(b),'ok:'+plan(a).Commit);assert.equal(total([a,b])['commit-tree'],2);assert.equal(a.physicalAfter.objects.filter(x=>x.startsWith(plan(a).Commit+' ')).length,1)
 assert.throws(()=>noEffects(b),assert.AssertionError);proof.push({name:'receipt-only-control',processes:2,sameGraphAndBindingExceptEvidenceRule:true,contractRejected:true,total:total([a,b]),uniqueCommitObjects:1,duplicateGitInvocation:true})
}
{
 const a=await execute('graph-control',1,{restorationDisabled:true}),b=await execute('graph-control',2,{restorationDisabled:true,withPrefix:true,inspectMember:true})
 assert.equal(outcome(b),'ok:'+plan(a).Commit);noEffects(b);assert.ok(!inspectReply(b).includes(plan(a).Commit));assert.throws(()=>assert.ok(inspectReply(b).includes(plan(a).Commit)),assert.AssertionError)
 proof.push({name:'graph-control',processes:2,sameBinding:true,restoredHeadContractRejected:true,total:total([a,b])})
}
{
 const rejected=command('capacity-rejected','begin','capacity-rejected',{target:3,mode:'spawn'}),admitted=command('later-admitted','begin','later-admitted',{target:4,mode:'spawn'})
 // Independent intact capacity sequence; no integration/Git dispatch occurs.
 const a=await execute('capacity',1,{withPrefix:false,customCommands:[command('hold1','begin','hold1',{target:1,mode:'spawn'}),command('hold2','begin','hold2',{target:2,mode:'spawn'}),rejected,command('stop1','stop','stop1',{target:1}),admitted,command('wait3','wait','wait3',{target:3}),command('stop2','stop','stop2',{target:2})],providerKinds:{1:'hold',2:'hold',3:'complete'}})
 const b=await execute('capacity',2,{customCommands:[rejected,admitted]});assert.equal(outcome(b,'capacity-rejected'),'err:unresolved');assert.deepEqual(response(b,'later-admitted'),{dispatched:true});unchanged(b);noProvider(b);noEffects(a)
 proof.push({name:'capacity',processes:2,repairedMemberAssociationPreserved:true})
}
await writeFile(join(dir,'physical-raw.json'),JSON.stringify(rows,null,2)+'\n')
const counts=total(rows),inspectionCommands=rows.reduce((n,r)=>n+r.events.filter(e=>e.operation==='physical-git-exit'&&!e.mutation).length,0)
await writeFile(join(dir,'physical-proof.json'),JSON.stringify({scope:'disposable standalone SHA1 repository, single plain-text file, one trusted writer, quiescent subprocess exits; no power loss',rows:rows.length,domains:new Set(rows.map(r=>r.name)).size,sigkills:rows.filter(r=>r.signal==='SIGKILL').length,proof,counts,inspectionCommands,headObservationSteps:rows.reduce((n,r)=>n+r.events.filter(e=>e.operation==='physical-head-observed').length,0),identity:{recordKey,token},hashes:{raw:hash(await readFile(join(dir,'physical-raw.json'))),source:hash(await readFile(new URL(import.meta.url)))}},null,2)+'\n')
console.log(`PASS: ${rows.length} physical processes; ${proof.length} contracts; ${rows.filter(r=>r.signal==='SIGKILL').length} SIGKILL windows; real Git mutations and read-only inspections counted separately`)
