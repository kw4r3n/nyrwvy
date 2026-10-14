// One application-gap probe: lose the integration response after an effect,
// then send the same bytes/ID to a fresh process. No durable state is supplied.
import assert from 'node:assert/strict'
import {readFile,writeFile,rm} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {createHash} from 'node:crypto'
import {arg} from '../offline-leader-jsonrpc/cases.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex'),rows=[]
const command=(id,op,label,extra={})=>JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:{op,label,...extra}}})
const prefix=[command(1,'begin','member',{target:1,mode:'spawn'}),command(2,'gate','provider1'),command(3,'gate','snapshot1'),command(4,'gate','release1'),command(5,'wait','settled',{target:1})]
const integration=command('same-integration-id','integrate','held',{target:1})
for(const lost of [true,false]){
 const gate=join(dir,'restart-release'),fixture=join(dir,'restart-scenario.json');await rm(gate,{force:true})
 await writeFile(fixture,JSON.stringify({kind:'transport',patch:'diff',providers:{1:'complete'},commitGateFile:gate}))
 const child=spawn('dotnet',[join(dir,'Runtime.dll'),fixture],{stdio:['pipe','pipe','pipe']})
 const events=[],outputs=[],actions=[],waiters=new Set();let failure,exitCode,signal,started=performance.now()
 const wake=()=>{for(const w of waiters)w()}
 for(const [stream,target] of [[child.stdout,outputs],[child.stderr,events]])createInterface({input:stream}).on('line',line=>{try{target.push(target===events?JSON.parse(line):line)}catch(e){failure=e}wake()})
 child.on('error',e=>{failure=e;wake()});child.stdin.on('error',e=>{failure=e;wake()})
 const ended=new Promise(resolve=>child.once('close',(code,sig)=>{exitCode=code;signal=sig;wake();resolve()}))
 const timer=setTimeout(()=>{failure=new Error('restart probe deadline');child.kill('SIGKILL');wake()},20000)
 const until=async pred=>{if(pred())return;await new Promise((resolve,reject)=>{const check=()=>{if(failure||exitCode!==undefined){waiters.delete(check);reject(failure??new Error('child exited before observation'))}else if(pred()){waiters.delete(check);resolve()}};waiters.add(check);check()})}
 const write=async line=>{const bytes=Buffer.from(line+'\n');actions.push({writeHex:bytes.toString('hex')});await new Promise((resolve,reject)=>child.stdin.write(bytes,e=>e?reject(e):resolve()))}
 const receipt=id=>until(()=>outputs.some(x=>JSON.parse(x).id===id))
 try{
  await until(()=>events.some(x=>x.operation==='read-start'))
  for(const line of prefix){await write(line);await receipt(JSON.parse(line).id)}
  await write(integration);await until(()=>events.some(x=>x.operation==='step'&&arg(x,'operation')==='commit'))
  if(lost){actions.push({closePeerOutput:true});child.stdout.destroy()}
  actions.push({releaseProtectedGate:true});await writeFile(gate,'release\n')
  if(!lost){await receipt('same-integration-id');actions.push({eof:true});child.stdin.end()}
  await ended;if(failure)throw failure
  const outcome=events.find(x=>x.operation==='outcome');assert.ok(outcome);assert.equal(exitCode,0);assert.equal(signal,null)
  assert.equal(outcome.Opened,1);assert.equal(outcome.Closed,1);assert.equal(outcome.Active,0)
  for(const phase of ['commit','head','save'])assert.equal(events.filter(x=>x.operation==='step'&&arg(x,'operation')===phase).length,1)
  assert.equal(arg(events.find(x=>x.operation==='step'&&arg(x,'operation')==='save'),'patch'),'integrated-head')
  assert.equal(events.filter(x=>x.operation==='input-fd-closed').length,1);assert.equal(events.filter(x=>x.operation==='transport-close').length,1)
  assert.equal(events.filter(x=>x.operation==='read-start').length,events.filter(x=>x.operation==='read-ended').length)
  if(lost){
   assert.equal(outcome.ok,false);assert.equal(outcome.type,'IOException');assert.equal(outcome.OutputFault,'IOException');assert.equal(outcome.TerminalWritten,false)
   assert.ok(!outputs.some(x=>JSON.parse(x).id==='same-integration-id'));assert.ok(!actions.some(x=>x.eof||x.signal))
   assert.equal(events.filter(x=>x.operation==='output-fault').length,1)
  }else{assert.equal(outcome.ok,true);assert.equal(outcome.TerminalWritten,true);assert.equal(JSON.parse(outputs.at(-1)).method,'session/terminal')}
  rows.push({lostResponse:lost,actions,events,outputs,outcome,exitCode,signal,elapsedMs:performance.now()-started})
 }finally{clearTimeout(timer);if(exitCode===undefined){child.kill('SIGKILL');await ended}}
}
assert.deepEqual(rows[0].actions.filter(x=>x.writeHex),rows[1].actions.filter(x=>x.writeHex),'same exact request bytes and correlation ID across restart')
const phases=Object.fromEntries(['commit','head','save'].map(p=>[p,rows.reduce((n,r)=>n+r.events.filter(x=>x.operation==='step'&&arg(x,'operation')===p).length,0)]))
assert.deepEqual(phases,{commit:2,head:2,save:2})
await writeFile(join(dir,'restart-raw.json'),JSON.stringify(rows,null,2)+'\n')
await writeFile(join(dir,'restart-proof.json'),JSON.stringify({gap:'durable deduplication/restart reconciliation',processes:2,sameRequestBytes:true,sameCorrelationId:'same-integration-id',firstResponseLostAfterMutation:true,firstOutcome:'IOException',secondOutcome:'success',repeatedEffects:phases,scope:'real Linux pipes and fresh processes; synthetic Git effects; no durable ledger or physical Git',hashes:{raw:hash(await readFile(join(dir,'restart-raw.json'))),source:hash(await readFile(new URL('./probe-restart.mjs',import.meta.url)))},elapsedMs:rows.map(x=>x.elapsedMs)},null,2)+'\n')
console.log('PASS: lost response after mutation; identical fresh-process retry repeats commit/HEAD/save (two each)')
