// Controlled offline application processes and disposable Git domains only.
import assert from 'node:assert/strict'
import {mkdir,readFile,writeFile,readdir,chmod} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {fixture,inspect,hash} from './git-fixture.mjs'
export const slot=k=>hash(k)+'.json'
export const store=async root=>Object.fromEntries(await Promise.all((await readdir(root)).sort().map(async name=>[name,await readFile(join(root,name),'utf8')])))
export const exists=async p=>{try{await readFile(p);return true}catch{return false}}
export const alive=async pid=>{try{return !(await readFile(`/proc/${pid}/stat`,'utf8')).split(') ')[1].startsWith('Z')}catch{return false}}
export async function until(pred,label){const end=Date.now()+25000;while(!await pred()){assert.ok(Date.now()<end,label);await new Promise(r=>setTimeout(r,10))}}
export const line=(id,op,extra={})=>JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:{op,label:op,...extra}}})
export async function suite(bundle,out){
 bundle=resolve(bundle);out=resolve(out);await mkdir(out,{recursive:true});assert.deepEqual(await readdir(out),[],'fresh output required')
 const rows=[],domains=[],owner=new URL('./git-owner.mjs',import.meta.url).pathname
 async function domain(name){const root=join(out,name);await mkdir(join(root,'state'),{recursive:true});const d={root,f:await fixture(join(root,'git')),seq:0};domains.push(d);return d}
 async function run(d,{prefix=false,crash='',held='',admin='',request,settings={},frames=[],storageName='state'}={}){
  const i=++d.seq,state=join(d.root,storageName),scenario={kind:'transport',patch:d.f.patch,providers:{1:'complete'},autoMember:'true',storageDir:state,commitGateFile:join(d.root,'commit-release'),journalGateFile:join(d.root,'journal-release'),snapshotCommit:d.f.selected,fixtureId:await readFile(join(d.f.root,'.git','offline-fixture-id'),'utf8'),gitOwner:owner,crashWindow:crash,...settings}
  if(admin){scenario[admin+'Request']=join(d.root,`request-${i}.json`);scenario[admin+'Authorization']='offline-owner-'+({disposition:'retain',continuation:'continue',release:'release',interruptedContinuation:'retain-continuation'})[admin];await writeFile(scenario[admin+'Request'],typeof request==='string'?request:JSON.stringify(request))}
  Object.assign(scenario,settings)
  const path=join(d.root,`scenario-${i}.json`);await writeFile(path,JSON.stringify(scenario))
  const before=await store(state),physicalBefore=await inspect(d.f.root),events=[],outputs=[],actions=[]
  const p=spawn('dotnet',[join(bundle,'Runtime.dll'),path],{stdio:['pipe','pipe','pipe'],env:{...process.env,...(held?{PATH:join(d.root,'bin')+':'+process.env.PATH,NYRWVY_HELD_ROOT:d.root,NYRWVY_HELD_COMMAND:held}:{})}})
  let code,signal,failure;const ended=new Promise(r=>p.once('close',(c,s)=>{code=c;signal=s;r()}));p.on('error',e=>failure=e);p.stdin.on('error',()=>{})
  for(const [stream,target] of [[p.stdout,outputs],[p.stderr,events]])createInterface({input:stream}).on('line',value=>{try{target.push(target===events?JSON.parse(value):value)}catch(e){failure=e}})
  const wait=pred=>until(()=>{if(failure)throw failure;if(code!==undefined&&!pred())throw Error('early exit '+JSON.stringify(events.slice(-3)));return pred()},d.root+'/'+i)
  const send=async input=>{const n=outputs.length;actions.push({writeHex:Buffer.from(input+'\n').toString('hex')});p.stdin.write(input+'\n');await wait(()=>outputs.length>n)}
  const timeout=setTimeout(()=>p.kill('SIGKILL'),30000)
  try{
   if(prefix){
    await wait(()=>events.some(e=>e.operation==='read-start'));await send(line(1,'begin',{target:1,mode:'spawn'}));await send(line(2,'wait',{target:1}))
    const input=line('original-id','integrate',{target:1});actions.push({writeHex:Buffer.from(input+'\n').toString('hex')});p.stdin.write(input+'\n');await writeFile(scenario.commitGateFile,'release')
   }else if(frames.length){
    await wait(()=>events.some(e=>e.operation==='read-start'));for(const input of frames)await send(input);p.stdin.end()
   }else p.stdin.end()
   if(held){await until(()=>exists(join(d.root,'held.json')),'held child');p.kill('SIGKILL');actions.push({killHeld:held})}
   else if(crash){await wait(()=>events.some(e=>e.operation==='journal-checkpoint'&&e.phase===crash));p.kill('SIGKILL');actions.push({killAt:crash})}
   else if(prefix){await wait(()=>outputs.some(x=>JSON.parse(x).id==='original-id'));p.stdin.end()}
   await ended;assert.equal(crash||held?signal:code,crash||held?'SIGKILL':0)
   const offered=actions.filter(a=>a.writeHex).map(a=>a.writeHex).join(''),read=events.filter(e=>e.operation==='pipe-read').map(e=>e.hex.toLowerCase()).join('');assert.ok(offered.startsWith(read));if(frames.length||prefix)assert.equal(read,offered)
   if(!admin)assert.equal(events.filter(e=>e.operation==='pipe-write').map(e=>e.hex.toLowerCase()).join(''),Buffer.from(outputs.map(x=>x+'\n').join('')).toString('hex'))
   const row={name:d.root.split('/').at(-1),index:i,scenario,request,actions,events,outputs,code,signal,before,after:await store(state),physicalBefore,physicalAfter:await inspect(d.f.root)};rows.push(row);await writeFile(join(d.root,`row-${i}.json`),JSON.stringify(row,null,2)+'\n');return row
  }finally{clearTimeout(timeout);if(code===undefined){p.kill('SIGKILL');await ended}}
 }
 async function wrapper(d){await mkdir(join(d.root,'bin'));await writeFile(join(d.root,'bin','git'),`#!/usr/bin/node
import fs from 'node:fs';import {execFileSync} from 'node:child_process';const args=process.argv.slice(2),root=process.env.NYRWVY_HELD_ROOT,input=fs.readFileSync(0);
if(args.includes(process.env.NYRWVY_HELD_COMMAND)){fs.writeFileSync(root+'/held.json',JSON.stringify({pid:process.pid,args}));while(!fs.existsSync(root+'/child-release'))await new Promise(r=>setTimeout(r,10));}
if(args.includes(process.env.NYRWVY_HELD_COMMAND)&&fs.existsSync(root+'/nonzero'))process.exit(7);
process.stdout.write(execFileSync('/usr/bin/git',args,{input}));
`);await chmod(join(d.root,'bin','git'),0o755)}
 async function retain(d,state){const map=await store(join(d.root,'state')),c=JSON.parse(map[slot('physical-child/v1')]);const q={id:'retain-id',authorization:'offline-owner-retain',action:'retain-unresolved',invocation:c.Invocation,identity:c.Identity,repository:d.f.root,repositoryId:await scenarioId(d),launchDigest:hash(map[slot('physical-child/v1')]),planDigest:hash(map[slot('physical-plan/v1/'+c.Identity)]),expectedState:state};const r=await run(d,{admin:'disposition',request:q});assert.deepEqual(r.outputs,['ok:retained-unresolved:'+state]);return q}
 const scenarioId=async d=>readFile(join(d.f.root,'.git','offline-fixture-id'),'utf8')
 return {rows,domains,domain,run,wrapper,retain,owner,out}
}
export function untouched(r){assert.deepEqual(r.before,r.after);assert.deepEqual(r.physicalBefore,r.physicalAfter);assert.ok(!r.events.some(e=>e.operation==='storage-write-attempt'||e.operation==='physical-git-owner-start'))}
export function uncertain(map){const c=JSON.parse(map[slot('physical-child/v1')]);assert.equal(map[slot('outcome/'+c.Identity)],undefined);assert.equal(map[slot('outcome-receipt/'+c.Identity)],undefined);const j=Object.values(map).map(x=>{try{return JSON.parse(x)}catch{return {}}}).find(x=>x.effectIdentity===c.Identity);assert.equal(j.state,'pending');assert.equal(j.outcome,'');assert.equal(j.outcomeSource,'')}
