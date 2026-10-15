import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {createHash} from 'node:crypto'
import {arg} from '../offline-leader-jsonrpc/cases.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const ping=id=>JSON.stringify({jsonrpc:'2.0',id,method:'ping'})
const command=(id,op,label,extra={})=>JSON.stringify({jsonrpc:'2.0',...(id===undefined?{}:{id}),method:'tools/call',params:{name:'session_command',arguments:{op,label,...extra}}})
const error=code=>`{"jsonrpc":"2.0","id":null,"error":{"code":${code},"message":"${code===-32700?'Parse error':'Invalid Request'}"}}`
const cases=[
 {id:'fragmented-utf8',mode:'fragment',lines:[ping('雪€😀"\n')]},
 {id:'coalesced-crlf-and-tail',mode:'batch',lines:[ping(1),'{',command(undefined,'inspect','notification'),ping('tail')],separator:'\r\n',tail:true},
 {id:'empty-eof',mode:'batch',lines:[]},
 {id:'valid-unterminated-eof',mode:'batch',lines:[ping('tail')],tail:true},
 {id:'truncated-json-eof',mode:'batch',lines:['{"jsonrpc":"2.0","id":7,"method":'],tail:true},
 {id:'invalid-envelopes-no-dispatch',mode:'batch',lines:['[]','{',command(3,'spawn','invalid',{extra:true}),ping(8)]},
 {id:'eof-active-member',mode:'member-eof',providers:{1:'hold'}},
 {id:'cancel-read-active-member',mode:'member-cancel',providers:{1:'hold'}},
 {id:'invalid-utf8-active-member',mode:'member-bad-utf8',providers:{1:'hold'}},
 {id:'truncated-utf8-active-member',mode:'member-short-utf8',providers:{1:'hold'}},
 {id:'cancel-queued-command',mode:'queued-cancel',providers:{1:'hold',2:'hold'}},
 {id:'eof-held-integration',mode:'integration-eof',providers:{1:'complete'},releaseOn:'eof'},
 {id:'cancel-held-integration',mode:'integration-cancel',providers:{1:'complete'}},
]
await writeFile(join(dir,'pipe-cases.json'),JSON.stringify(cases,null,2)+'\n')
const retained=[]
function contract(name,check){try{return check()}catch(error){error.contract=name;throw error}}
function acceptance(c,row,{responses,releaseCount=1}={}){
 const events=row.events,outputs=row.outputs,outcome=events.find(x=>x.operation==='outcome'),calls=events.filter(x=>x.arguments)
 contract('application-outcome',()=>assert.ok(outcome,'child must publish an outcome'))
 assert.equal(outcome.Opened,1);assert.equal(outcome.Active,0,'providers joined before release')
 contract('resource-release-count',()=>{
  assert.equal(outcome.Closed,releaseCount,'exactly-once resource cleanup')
  assert.equal(events.filter(x=>x.operation==='close').length,releaseCount,'exactly one close effect')
 })
 assert.equal(events.filter(x=>x.operation==='process-terminal').length,1)
 assert.equal(events.filter(x=>x.operation==='transport-close').length,1,'exactly one drained pipe close')
 assert.ok(events.findIndex(x=>x.operation==='input-drain-start')<events.findIndex(x=>x.operation==='transport-close'))
 assert.ok(events.findIndex(x=>x.operation==='transport-close')<events.findIndex(x=>x.operation==='process-terminal'),'process terminal follows joined transport close')
 assert.equal(row.exitCode,0);assert.equal(row.signal,null)
 const terminal=outputs.filter(x=>JSON.parse(x).method==='session/terminal')
 const success=!c.mode.includes('cancel')&&!c.mode.includes('utf8')
 if(success){
  contract('application-success',()=>{assert.equal(outcome.ok,true);assert.equal(outcome.isOk,true)})
  assert.equal(terminal.length,1)
  assert.equal(outputs.at(-1),terminal[0]);assert.equal(JSON.parse(terminal[0]).params.report,outcome.value)
  assert.ok(events.findIndex(x=>x.operation==='close')<events.findIndex(x=>x.operation==='reply'&&arg(x,'label')==='@wire-terminal'),'release precedes wire terminal')
  assert.ok(events.findIndex(x=>x.operation==='input-eof')<events.findIndex(x=>x.operation==='close'))
  assert.ok(calls.some(x=>x.operation==='reply'&&arg(x,'label')==='@requests-joined'))
 }else{
  assert.equal(terminal.length,0,'transport failure must not publish successful terminal')
  if(c.mode.includes('utf8')){assert.equal(outcome.ok,false);assert.equal(outcome.type,'DecoderFallbackException')}
  else {
   assert.equal(outcome.ok,false,'root cancellation must escape the pipe wait')
   assert.equal(outcome.type,'TaskCanceledException','ordinary transport faults cannot stand in for cancellation')
   assert.equal(events.filter(x=>x.operation==='transport-cancel').length,1)
   assert.ok(events.findIndex(x=>x.operation==='outcome')<events.findIndex(x=>x.operation==='input-drain-start'),'application settles while peer stdin is still open')
   const drained=events.findIndex(x=>x.operation==='pipe-read'&&x.hex==='')
   assert.ok(drained>events.findIndex(x=>x.operation==='input-drain-start')&&drained<events.findIndex(x=>x.operation==='transport-close'),'pending OS read actually drains at peer EOF before transport close')
  }
 }
 if(c.lines){
  const expected=c.lines.flatMap(line=>{
   if(line==='{'||line.startsWith('{"jsonrpc":"2.0","id":7,"method":'))return [error(-32700)]
   if(line==='[]')return [error(-32600)]
   const value=JSON.parse(line);if(!Object.hasOwn(value,'id'))return []
   if(value.method==='ping')return [`{"jsonrpc":"2.0","id":${JSON.stringify(value.id)},"result":{}}`]
   return ['{"jsonrpc":"2.0","id":3,"error":{"code":-32602,"message":"Invalid params"}}']
  })
  contract('response-correlation-order',()=>assert.deepEqual(outputs.filter(x=>!JSON.parse(x).method),responses??expected,'exact correlated ordered responses'))
  assert.ok(!calls.some(x=>['workspaceCreate','provider','snapshot','step'].includes(x.operation)),'rejected/notification inspection frames have no mutation')
 }else{
  const count=c.mode.startsWith('integration')?7:c.mode==='queued-cancel'?6:2
  assert.deepEqual(outputs.filter(x=>!JSON.parse(x).method),Array.from({length:count},(_,i)=>`{"jsonrpc":"2.0","id":${i+1},"result":{"dispatched":true}}`),'every in-flight command receipt is exact, ordered and once-only')
 }
 if(c.mode.startsWith('member')||c.mode==='queued-cancel'){
  assert.equal(calls.filter(x=>x.operation==='memberClose'&&arg(x,'id')==='1').length,1,'once-only Member cleanup')
  assert.ok(events.findIndex(x=>x.operation==='memberClose')<events.findIndex(x=>x.operation==='close'))
  assert.ok(!calls.some(x=>x.operation==='snapshot'||x.operation==='step'))
 }
 if(c.mode==='queued-cancel'){
  assert.ok(calls.some(x=>x.operation==='gate'&&arg(x,'label')==='request-start:cancelled'))
  assert.ok(!calls.some(x=>x.operation==='workspaceCreate'&&x.request==='cancelled'),'cancelled queued spawn cannot create workspace')
 }
 if(c.mode.startsWith('integration')){
  for(const phase of ['check','apply','commit','head','save'])assert.equal(calls.filter(x=>x.operation==='step'&&arg(x,'operation')===phase).length,1,`${phase} exactly once`)
  assert.ok(calls.some(x=>x.operation==='step'&&arg(x,'operation')==='save'&&arg(x,'patch')==='integrated-head'),'protected mutation saves its completed HEAD')
  assert.ok(calls.some(x=>x.operation==='reply'&&arg(x,'label')==='held'&&arg(x,'record')===(c.mode.endsWith('eof')?'ok:integrated-head':'err:cancelled')),'command result preserves inherited cancellation semantics')
  const end=events.findIndex(x=>x.operation===(c.mode.endsWith('eof')?'input-eof':'transport-cancel'))
  assert.ok(events.findIndex(x=>x.operation==='step'&&arg(x,'operation')==='commit')<end,'terminal condition arrives during held mutation')
  assert.ok(end<events.findIndex(x=>x.operation==='step'&&arg(x,'operation')==='head'),'protected mutation completes after terminal condition')
 }
 return {id:c.id,outputs:outputs.length,success,releaseCount,activeAtRelease:0}
}
// A rejected positive contract is necessary but insufficient: each negative
// row must execute its intended defect and otherwise meet its paired lifecycle.
function controlCause(c,row,control=row.control){
 const events=row.events,of=operation=>events.filter(x=>x.operation===operation)
 const index=operation=>events.findIndex(x=>x.operation===operation)
 if(control==='drop-tail'){
  acceptance(c,row,{responses:[]})
  const drops=of('tail-dropped');assert.equal(drops.length,1);assert.equal(drops[0].line,c.lines[0])
  assert.ok(index('readLine')<index('tail-dropped')&&index('tail-dropped')<index('input-eof'))
  assert.equal(of('input-eof').length,1)
  assert.ok(of('pipe-read').some(x=>x.synchronous===true&&x.hex===''),'EOF probe actually completed')
  assert.ok(!events.some(x=>x.operation==='reply'&&arg(x,'label')==='@wire'),'dropped frame never reaches wire response')
  return {contract:'response-correlation-order',evidence:{healthyOutcome:true,droppedLine:drops[0].line,missingResponseId:'tail',releaseCount:1}}
 }
 if(control==='wrong-id'){
  const before='{"jsonrpc":"2.0","id":"tail","result":{}}',after='{"jsonrpc":"2.0","id":"wrong","result":{}}'
  acceptance(c,row,{responses:[after]})
  assert.deepEqual(of('response-id-rewritten'),[{operation:'response-id-rewritten',before,after}])
  assert.ok(events.some(x=>x.operation==='reply'&&arg(x,'label')==='@wire'&&arg(x,'record')===before),'graph produces the correct ID before host corruption')
  return {contract:'response-correlation-order',evidence:{healthyOutcome:true,graphResponse:before,deliveredResponse:after,releaseCount:1}}
 }
 if(control==='double-close'){
  acceptance(c,row,{releaseCount:2})
  assert.deepEqual(of('duplicate-close'),[{operation:'duplicate-close',count:2}])
  return {contract:'resource-release-count',evidence:{healthyOutcome:true,closeCount:2}}
 }
 assert.equal(control,'uninterruptible-read')
 assert.equal(row.deadlineExceeded,'pipe test deadline');assert.equal(row.signal,'SIGKILL');assert.equal(row.exitCode,null)
 assert.equal(of('transport-cancel').length,1);assert.ok(row.actions.some(x=>x.signal==='SIGTERM'))
 assert.ok(!row.actions.some(x=>x.eof),'peer never supplies the EOF that could release this blocked read')
 const wait=events.findLastIndex(x=>x.operation==='uninterruptible-read-wait')
 assert.ok(wait>events.findLastIndex(x=>x.operation==='readLine')&&wait<index('transport-cancel'),'cancellation arrives during the deliberately uninterruptible wait')
 assert.equal(events.filter(x=>x.operation==='memberClose'&&arg(x,'id')==='1').length,1,'provider actually observes cancellation and cleans up')
 for(const operation of ['outcome','close','input-drain-start','transport-close','process-terminal','input-drain-fault'])assert.equal(of(operation).length,0,operation)
 assert.deepEqual(row.outputs,[1,2].map(id=>`{"jsonrpc":"2.0","id":${id},"result":{"dispatched":true}}`),'all preceding requests completed normally')
 return {contract:'application-outcome',evidence:{waitObserved:true,cancellationDelivered:true,memberCleanups:1,watchdogReaped:true,noApplicationRelease:true}}
}
async function execute(c,control=''){
 const fixture=join(dir,'scenario.json');await writeFile(fixture,JSON.stringify({kind:'transport',patch:'diff',providers:{},...c}))
 const child=spawn('dotnet',[join(dir,'Runtime.dll'),fixture,control],{stdio:['pipe','pipe','pipe']})
 const events=[],outputs=[],actions=[];let failure,exitCode,signal
 const waiters=new Set()
 const wake=()=>{for(const waiter of waiters)waiter()}
 for(const [stream,target] of [[child.stdout,outputs],[child.stderr,events]]){
  createInterface({input:stream}).on('line',line=>{try{target.push(target===events?JSON.parse(line):line)}catch(error){failure=error}wake()})
 }
 child.on('error',error=>{failure=error;wake()});child.stdin.on('error',error=>{failure=error;wake()})
 const ended=new Promise(resolve=>child.once('close',(code,sig)=>{exitCode=code;signal=sig;wake();resolve()}))
 const deadline=()=>{failure=new Error('pipe test deadline');child.kill('SIGKILL');wake()}
 let timer=setTimeout(deadline,20000)
 async function until(predicate){
  if(predicate())return
  await new Promise((resolve,reject)=>{const check=()=>{if(failure||exitCode!==undefined){waiters.delete(check);reject(failure??new Error('child ended before gate'))}else if(predicate()){waiters.delete(check);resolve()}};waiters.add(check);check()})
 }
 const write=async bytes=>{actions.push({writeHex:bytes.toString('hex')});await new Promise((resolve,reject)=>child.stdin.write(bytes,error=>error?reject(error):resolve()))}
 const send=async line=>{const count=events.filter(x=>x.operation==='readLine').length;await write(Buffer.from(line+'\n'));await until(()=>events.filter(x=>x.operation==='readLine').length>count)}
 const receipt=async id=>until(()=>outputs.some(x=>JSON.parse(x).id===id))
 const gate=async (name,id)=>{await send(command(id,'gate',name));await receipt(id)}
 const eof=()=>{actions.push({eof:true});child.stdin.end()}
 const cancel=()=>{actions.push({signal:'SIGTERM'});child.kill('SIGTERM')}
 try{
  await until(()=>events.some(x=>x.operation==='read-start'))
  if(c.mode==='fragment'){
   const bytes=Buffer.from(c.lines[0]+'\n'),cut=bytes.indexOf(Buffer.from('€'))+1
   await write(bytes.subarray(0,cut));await until(()=>events.some(x=>x.operation==='pipe-read'))
   assert.equal(outputs.length,0,'incomplete UTF8 frame must not dispatch');assert.equal(events.filter(x=>x.operation==='readLine').length,0)
   await write(bytes.subarray(cut));await receipt('雪€😀"\n');eof()
  }else if(c.mode==='batch'){
   await write(Buffer.from(c.lines.join(c.separator??'\n')+(c.lines.length&&!c.tail?(c.separator??'\n'):'')));eof()
  }else{
   await send(command(1,'begin','member',{target:1,mode:'spawn'}));await receipt(1)
   await gate('provider1',2)
   if(c.mode.startsWith('integration')){
    await gate('snapshot1',3);await gate('release1',4)
    await send(command(5,'wait','settled',{target:1}));await receipt(5)
    await send(command(6,'begin','held',{target:2,previous:1,mode:'integrate'}));await receipt(6)
    await gate('commit-held',7)
   }else if(c.mode==='queued-cancel'){
    await send(command(3,'begin','blocker',{target:2,mode:'spawn'}));await receipt(3);await gate('spawn-held',4)
    await send(command(5,'begin','cancelled',{target:3,mode:'spawn'}));await receipt(5);await gate('queued:cancelled',6)
   }
   const reads=events.filter(x=>x.operation==='readLine').length
   await until(()=>events.filter(x=>x.operation==='read-start').length>reads)
   if(c.mode.endsWith('eof'))eof()
   else if(c.mode==='member-bad-utf8'){await write(Buffer.from([0xff,0x0a]));eof()}
   else if(c.mode==='member-short-utf8'){await write(Buffer.from([0xe2,0x82]));eof()}
   else {
    if(control==='uninterruptible-read'){
     await until(()=>events.filter(x=>x.operation==='uninterruptible-read-wait').length>reads)
     clearTimeout(timer);timer=setTimeout(deadline,2000)
    }
    cancel();await until(()=>events.some(x=>x.operation==='input-drain-start'));eof()
   }
  }
  await ended
  const row={id:c.id,control,scenario:{kind:'transport',patch:'diff',providers:{},...c},actions,events,outputs,exitCode,signal}
  retained.push(row)
  if(failure)throw failure
  return row
 }catch(error){
  if(control!=='uninterruptible-read')throw error
  await ended
  const row={id:c.id,control,scenario:{kind:'transport',patch:'diff',providers:{},...c},actions,events,outputs,exitCode,signal,deadlineExceeded:error.message}
  retained.push(row);return row
 }finally{clearTimeout(timer);if(exitCode===undefined){child.kill('SIGKILL');await ended}}
}
const proof=[],verificationGuards=[]
try{
 for(const c of cases){const row=await execute(c);proof.push(acceptance(c,row));console.log(`PASS: actual pipes ${c.id}`)}
 for(const [id,control] of [['valid-unterminated-eof','drop-tail'],['valid-unterminated-eof','wrong-id'],['empty-eof','double-close'],['cancel-read-active-member','uninterruptible-read']]){
  const c=cases.find(x=>x.id===id),row=await execute(c,control)
  const cause=controlCause(c,row)
  let rejection;try{acceptance(c,row)}catch(error){rejection=error}
  assert.ok(rejection,`${control} must fail the identical positive acceptance`)
  assert.equal(rejection.contract,cause.contract,`${control} must fail its intended contract`)
  proof.push({id,control,rejected:true,contract:cause.contract,cause:cause.evidence,reason:rejection.message});console.log(`PASS: compiled ${control} defect observed; intended contract rejected`)
 }
 // Compiled guard against the old false positive: unrelated host exceptions
 // must be refused as tail-loss evidence, even though positive acceptance fails.
 const c=cases.find(x=>x.id==='valid-unterminated-eof'),row=await execute(c,'drop-tail-fault')
 assert.equal(row.events.filter(x=>x.operation==='tail-control-fault').length,1)
 assert.equal(row.events.find(x=>x.operation==='outcome').type,'NotSupportedException')
 let rejected;try{controlCause(c,row,'drop-tail')}catch(error){rejected=error}
 assert.equal(rejected?.contract,'application-success','unhealthy control cannot masquerade as intended tail loss')
 verificationGuards.push({id:'unrelated-tail-control-fault',refusedAsTailLoss:true,contract:rejected.contract,observedType:'NotSupportedException'})
 console.log('PASS: compiled unrelated exception refused as tail-loss evidence')
}finally{await writeFile(join(dir,'pipe-raw.json'),JSON.stringify(retained,null,2)+'\n')}
const hashes={};for(const file of ['session.json','Generated.cs','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','symbols.json','declarations.json','authoring.json','pipe-cases.json','compilation.json','pipe-raw.json'])hashes[file]=hash(await readFile(join(dir,file)))
const sourceHashes={}
async function source(path){
 if(sourceHashes[path])return
 const bytes=await readFile(path);sourceHashes[path]=hash(bytes)
 for(const match of bytes.toString().matchAll(/(?:from\s+|import\s*)['"]([^'"]+)['"]/g)){
  if(match[1].startsWith('.'))await source(resolve(path,'..',match[1]))
 }
}
for(const name of ['author.mjs','run.mjs','compile.mjs','test-runtime.mjs','verify.mjs','pipe.template.cs'])await source(resolve('examples/offline-session-transport',name))
await source(resolve('examples/offline-leader-jsonrpc/runtime.template.cs'))
await source(resolve('examples/dynamic-worker-collector/compile-runtime.mjs'))
await writeFile(join(dir,'pipe-proof.json'),JSON.stringify({base:'9171508041350969a8d41e0408d9e6b4cba55dd5',baseTree:'93a914f2f36bb922fb0c1f3e2aed8b9b42b175ed',dependency:'PR396 stacked on unmerged PR395',execution:'actual OS child-process stdin/stdout pipes, compiled unedited graph, strict platform UTF-8 line reader; synthetic effects/gates',proof,verificationGuards,hashes,sourceHashes:Object.fromEntries(Object.entries(sourceHashes).map(([path,value])=>[path.slice(resolve('.').length+1),value]))},null,2)+'\n')
