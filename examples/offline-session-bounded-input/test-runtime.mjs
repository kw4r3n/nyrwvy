import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {createHash} from 'node:crypto'
import {arg} from '../offline-leader-jsonrpc/cases.mjs'
import {maxFrameBytes as limit} from './author.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const ping=id=>JSON.stringify({jsonrpc:'2.0',id,method:'ping'})
const command=(id,op,label,extra={})=>JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:{op,label,...extra}}})
const parseError='{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}'
const response=id=>`{"jsonrpc":"2.0","id":${JSON.stringify(id)},"result":{}}`
const validBoundary=ping('😀'.repeat(4096-Array.from(ping('')).length))
assert.equal(Array.from(validBoundary).length,4096)
const cases=[
 {id:'oversize-lf',mode:'oversize',size:limit+1,terminator:'\n'},
 {id:'oversize-crlf',mode:'oversize',size:limit+1,terminator:'\r\n'},
 {id:'oversize-unterminated',mode:'oversize',size:limit+1},
 {id:'one-mib-unterminated',mode:'queued-oversize',size:1024*1024},
 {id:'oversize-member',mode:'oversize',member:true,size:limit+1},
 {id:'exact-limit-lf',mode:'boundary',size:limit,terminator:'\n'},
 {id:'exact-limit-crlf-split',mode:'boundary-crlf',size:limit},
 {id:'exact-limit-eof',mode:'boundary-eof',size:limit},
 {id:'exact-limit-retained-cancel',mode:'boundary-cancel',size:limit},
 {id:'partial-multibyte-cancel',mode:'partial-cancel',size:limit},
 {id:'valid-4096-codepoints-multibyte',mode:'valid-boundary'},
 {id:'fragmented-multibyte',mode:'fragment'},
 {id:'byte-limit-before-multibyte-decode',mode:'multibyte-oversize',size:limit+1},
 {id:'invalid-utf8-member',mode:'bad-utf8',member:true},
 {id:'incomplete-utf8-exact-limit-eof',mode:'short-utf8',size:limit},
 {id:'malformed-json-and-tail',mode:'malformed'},
 {id:'empty-eof',mode:'empty'},
 {id:'coalesced-cr-lf-crlf-tail',mode:'coalesced'},
 {id:'valid-prefix-before-bad-utf8',mode:'prefix-bad-utf8'},
 {id:'foreground-reader-fault-deferred',mode:'foreground-oversize',member:true,size:limit+1},
]
await writeFile(join(dir,'pipe-cases.json'),JSON.stringify(cases,null,2)+'\n')
const retained=[],proof=[],guards=[]
const of=(row,op)=>row.events.filter(x=>x.operation===op)
function lifecycle(row){
 const outcome=of(row,'outcome')[0];assert.ok(outcome,'actual application outcome required')
 assert.equal(row.exitCode,0);assert.equal(row.signal,null);assert.equal(row.deadlineExceeded,undefined)
 assert.equal(outcome.Opened,1);assert.equal(outcome.Closed,1);assert.equal(outcome.Active,0)
 for(const op of ['close','transport-close','input-fd-closed','process-terminal'])assert.equal(of(row,op).length,1,op)
 assert.equal(of(row,'transport-close')[0].activeReads,0)
 assert.equal(of(row,'read-start').length,of(row,'read-ended').length)
 const index=op=>row.events.findIndex(x=>x.operation===op)
 assert.ok(index('close')<index('transport-close')&&index('transport-close')<index('process-terminal'))
 assert.ok(row.events.findLastIndex(x=>x.operation==='read-ended')<index('input-fd-closed'))
 if(row.scenario.member){
  assert.equal(row.events.filter(x=>x.operation==='memberClose'&&arg(x,'id')==='1').length,1)
  assert.ok(index('memberClose')<index('close'))
 }
 return outcome
}
function acceptance(c,row){
 const outcome=lifecycle(row),oversize=c.mode.includes('oversize'),cancel=c.mode.includes('cancel')
 const invalid=c.mode.includes('utf8'),foreground=c.mode==='foreground-oversize'
 const decoded=of(row,'decode-start'),over=of(row,'input-oversize')
 assert.ok(decoded.every(x=>x.payloadBytes<=limit&&x.maxBytes===limit),'decode is bounded by graph byte policy')
 assert.ok(of(row,'read-start').every(x=>x.maxBytes===limit),'ordinary graph invocation supplies byte limit')
 assert.ok(of(row,'read-ended').every(x=>x.bufferedBytes<=limit),'payload buffer never exceeds limit')
 const replies=row.outputs.filter(x=>!JSON.parse(x).method),initial=c.member?[1,2].map(id=>`{"jsonrpc":"2.0","id":${id},"result":{"dispatched":true}}`):[]
 if(oversize){
  assert.deepEqual(over,[{operation:'input-oversize',maxBytes:limit,bufferedBytes:limit,observedPayloadBytes:limit+1,readBufferBytes:1024}],'exact first excess byte causes failure before decode')
  assert.equal(outcome.ok,false);assert.equal(outcome.type,'InvalidDataException','reader fault is preserved even after foreground signal recovery')
  assert.equal(decoded.length,foreground?6:initial.length,'oversized frame never enters decoding')
  assert.equal(of(row,'readLine').length,decoded.length)
  if(!foreground){assert.equal(outcome.message,'pipe frame exceeds byte limit');assert.deepEqual(replies,initial)}
  assert.ok(!row.actions.some(x=>x.eof),'oversize cleanup needs neither delimiter nor peer EOF')
 }else{
  assert.equal(over.length,0)
  if(cancel){
   assert.equal(outcome.ok,false);assert.equal(outcome.type,'TaskCanceledException')
   assert.equal(decoded.length,0);assert.equal(of(row,'readLine').length,0)
   assert.ok(!row.actions.some(x=>x.eof));assert.ok(row.measurements.afterCancelMs<2000)
  }else if(invalid){assert.equal(outcome.ok,false);assert.equal(outcome.type,'DecoderFallbackException')}
  else{
   assert.equal(outcome.ok,true);assert.equal(outcome.isOk,true)
   assert.equal(outcome.LocalCompleted,true);assert.equal(outcome.TerminalWritten,true)
   assert.equal(of(row,'input-eof').length,1)
   assert.equal(JSON.parse(row.outputs.at(-1)).method,'session/terminal')
  }
 }
 if(oversize||cancel||invalid){assert.equal(outcome.TerminalWritten,false);assert.ok(!row.outputs.some(x=>JSON.parse(x).method==='session/terminal'))}
 if(c.mode.startsWith('boundary')&&!cancel){
  assert.equal(decoded[0].payloadBytes,limit);assert.deepEqual(replies,c.mode==='boundary-eof'?[parseError]:[parseError,response('after')])
 }
 if(c.mode==='short-utf8')assert.equal(decoded[0].payloadBytes,limit)
 if(c.mode==='valid-boundary')assert.deepEqual(replies,[response(JSON.parse(validBoundary).id)])
 if(c.mode==='fragment')assert.deepEqual(replies,[response('é雪€😀')])
 if(c.mode==='malformed')assert.deepEqual(replies,[parseError,response('after'),parseError])
 if(c.mode==='coalesced')assert.deepEqual(replies,[1,2,3,'tail'].map(response))
 if(c.mode==='prefix-bad-utf8')assert.deepEqual(replies,[response('before')])
 if(!c.member)assert.ok(!row.events.some(x=>['workspaceCreate','provider','snapshot','step'].includes(x.operation)))
 if(foreground){
  assert.deepEqual(row.actions.find(x=>x.observedBlocked)?.observedBlocked,{ms:350,outcome:false,head:false,release:false})
  assert.equal(of(row,'transport-cancel').length,1)
  for(const phase of ['check','apply','commit','head','save'])assert.equal(row.events.filter(x=>x.operation==='step'&&arg(x,'operation')===phase).length,1)
  assert.ok(row.events.some(x=>x.operation==='step'&&arg(x,'operation')==='save'&&arg(x,'patch')==='integrated-head'))
  assert.deepEqual(replies,[1,2,3,4,5,6].map(id=>`{"jsonrpc":"2.0","id":${id},"result":{"dispatched":true}}`))
  assert.ok(row.measurements.afterCancelMs<2000)
 }
 return {id:c.id,releaseCount:1,outcomeType:outcome.type??'success',maxBufferedBytes:Math.max(0,...of(row,'read-ended').map(x=>x.bufferedBytes)),oversizeRejected:oversize,foregroundFaultDeferred:foreground}
}
async function execute(c,control=''){
 const fixture=join(dir,'scenario.json'),scenario={kind:'transport',patch:'diff',providers:c.member?{1:c.mode==='foreground-oversize'?'complete':'hold'}:{},...c}
 await writeFile(fixture,JSON.stringify(scenario))
 const child=spawn('dotnet',[join(dir,...(control==='relaxed-policy'?['relaxed','Runtime.dll']:['Runtime.dll'])),fixture,control],{stdio:['pipe','pipe','pipe']})
 const events=[],outputs=[],actions=[],waiters=new Set();let failure,exitCode,signal,cancelledAt,peerWriteError
 const wake=()=>{for(const waiter of waiters)waiter()}
 for(const [stream,target] of [[child.stdout,outputs],[child.stderr,events]])createInterface({input:stream}).on('line',line=>{try{target.push(target===events?JSON.parse(line):line)}catch(e){failure=e}wake()})
 child.on('error',error=>{failure=error;wake()});child.stdin.on('error',error=>{peerWriteError=error.code;wake()})
 const ended=new Promise(resolve=>child.once('close',(code,sig)=>{exitCode=code;signal=sig;wake();resolve()}))
 const timer=setTimeout(()=>{failure=new Error('pipe test deadline');child.kill('SIGKILL');wake()},20000)
 const until=async predicate=>{
  if(predicate())return
  await new Promise((resolve,reject)=>{const check=()=>{if(failure||exitCode!==undefined){waiters.delete(check);reject(failure??new Error('child ended before observation'))}else if(predicate()){waiters.delete(check);resolve()}};waiters.add(check);check()})
 }
 const write=async bytes=>{actions.push({writeHex:bytes.toString('hex')});await new Promise((resolve,reject)=>child.stdin.write(bytes,e=>e?reject(e):resolve()))}
 const queued=bytes=>{actions.push({writeQueuedHex:bytes.toString('hex')});child.stdin.write(bytes,e=>{if(e)peerWriteError=e.code})}
 const eof=()=>{actions.push({eof:true});child.stdin.end()}
 const cancel=()=>{actions.push({signal:'SIGTERM'});cancelledAt=performance.now();child.kill('SIGTERM')}
 const receipt=id=>until(()=>outputs.some(x=>JSON.parse(x).id===id))
 const send=async line=>{await write(Buffer.from(line+'\n'));await receipt(JSON.parse(line).id)}
 const waitAt=bytes=>until(()=>events.some(x=>x.operation==='input-wait'&&x.bufferedBytes===bytes))
 try{
  await until(()=>events.some(x=>x.operation==='read-start'))
  if(c.member){await send(command(1,'begin','member',{target:1,mode:'spawn'}));await send(command(2,'gate','provider1'))}
  if(control==='unrelated-input-fault'){await write(Buffer.from(ping('guard')+'\n'));eof()}
  else if(c.mode==='foreground-oversize'){
   await send(command(3,'gate','snapshot1'));await send(command(4,'gate','release1'));await send(command(5,'wait','settled',{target:1}))
   await write(Buffer.from(command(6,'integrate','held',{target:1})+'\n'))
   await until(()=>events.some(x=>x.operation==='step'&&arg(x,'operation')==='commit'))
   await write(Buffer.alloc(c.size,65));await until(()=>events.some(x=>x.operation==='input-oversize'))
   await new Promise(r=>setTimeout(r,350))
   actions.push({observedBlocked:{ms:350,outcome:events.some(x=>x.operation==='outcome'),head:events.some(x=>x.operation==='step'&&arg(x,'operation')==='head'),release:events.some(x=>x.operation==='close')}})
   cancel()
  }else if(c.mode==='queued-oversize'){
   const bytes=Buffer.alloc(c.size,65);queued(bytes)
   if(control==='relaxed-policy'){await waitAt(c.size);actions.push({observedUnbounded:{bytes:c.size,decoded:events.filter(x=>x.operation==='decode-start').length,oversize:events.some(x=>x.operation==='input-oversize')}});cancel()}
  }else if(c.mode==='oversize'){
   await write(Buffer.concat([Buffer.alloc(c.size,65),Buffer.from(c.terminator??'')]))
   if(control==='relaxed-policy'){
    if(c.terminator){await until(()=>events.some(x=>x.operation==='readLine'));eof()}
    else{await waitAt(c.size);actions.push({observedUnbounded:{bytes:c.size,decoded:0,oversize:false}});cancel()}
   }
  }else if(c.mode==='multibyte-oversize'){
   await write(Buffer.concat([Buffer.alloc(limit-2,65),Buffer.from([0xe2,0x82])]))
   await waitAt(limit);assert.equal(events.filter(x=>x.operation==='decode-start').length,0)
   await write(Buffer.from([0xac,0x0a]))
  }else if(c.mode==='boundary'||c.mode==='boundary-crlf'||c.mode==='boundary-eof'||c.mode==='boundary-cancel'){
   await write(Buffer.alloc(c.size,65));await waitAt(limit)
   assert.equal(events.filter(x=>x.operation==='decode-start').length,0,'exact limit alone must wait')
   actions.push({observedBoundary:{bytes:limit,decoded:0,oversize:false}})
   if(c.mode==='boundary-eof')eof()
   else if(c.mode==='boundary-cancel')cancel()
   else if(c.mode==='boundary-crlf'){
    await write(Buffer.from('\r'));await until(()=>outputs.includes(parseError));await write(Buffer.from('\n'+ping('after')+'\n'));await receipt('after');eof()
   }else{await write(Buffer.from(c.terminator+ping('after')+'\n'));await receipt('after');eof()}
  }else if(c.mode==='partial-cancel'||c.mode==='short-utf8'){
   await write(Buffer.concat([Buffer.alloc(limit-2,65),Buffer.from([0xe2,0x82])]))
   await waitAt(limit);if(c.mode==='partial-cancel')cancel();else eof()
  }else if(c.mode==='valid-boundary'){await send(validBoundary);eof()}
  else if(c.mode==='fragment'){
   const bytes=Buffer.from(ping('é雪€😀')+'\n')
   for(const token of ['é','雪','€','😀']){
    const start=bytes.indexOf(Buffer.from(token)),before=actions.filter(x=>x.writeHex).reduce((n,x)=>n+x.writeHex.length/2,0)
    for(let end=start+1;end<start+Buffer.byteLength(token);end++){
     const supplied=actions.filter(x=>x.writeHex).reduce((n,x)=>n+x.writeHex.length/2,0)
     await write(bytes.subarray(supplied,end));await waitAt(end)
     assert.equal(outputs.length,0);assert.equal(events.filter(x=>x.operation==='decode-start').length,0)
    }
    assert.ok(start>=before)
   }
   const supplied=actions.filter(x=>x.writeHex).reduce((n,x)=>n+x.writeHex.length/2,0)
   await write(bytes.subarray(supplied));await receipt('é雪€😀');eof()
  }else if(c.mode==='bad-utf8'){await write(Buffer.from([0xff,0x0a]));eof()}
  else if(c.mode==='prefix-bad-utf8'){await write(Buffer.concat([Buffer.from(ping('before')+'\n'),Buffer.from([0xff,0x0a]),Buffer.from(ping('after')+'\n')]));eof()}
  else if(c.mode==='malformed'){await write(Buffer.from('{\n'+ping('after')+'\n{"jsonrpc":'));eof()}
  else if(c.mode==='coalesced'){await write(Buffer.from(ping(1)+'\r'+ping(2)+'\n'+ping(3)+'\r\n'+ping('tail')));eof()}
  else if(c.mode==='empty')eof()
  else throw Error('unhandled mode '+c.mode)
  await ended
  if(failure)throw failure
  const row={id:c.id,control,scenario,actions,events,outputs,exitCode,signal,measurements:{...(cancelledAt===undefined?{}:{afterCancelMs:performance.now()-cancelledAt}),...(peerWriteError?{peerWriteError}:{})}}
  retained.push(row);return row
 }finally{clearTimeout(timer);if(exitCode===undefined){child.kill('SIGKILL');await ended}}
}
try{
 for(const c of cases){const row=await execute(c);proof.push(acceptance(c,row));console.log(`PASS: actual bounded pipes ${c.id}`)}
 for(const id of ['oversize-lf','oversize-unterminated','one-mib-unterminated']){
  const c=cases.find(x=>x.id===id),row=await execute(c,'relaxed-policy'),outcome=lifecycle(row)
  assert.ok(of(row,'read-start').every(x=>x.maxBytes===2*1024*1024),'same host receives separately authored relaxed graph policy')
  assert.equal(of(row,'input-oversize').length,0)
  if(c.terminator){assert.equal(outcome.ok,true);assert.equal(of(row,'decode-start')[0].payloadBytes,c.size);assert.equal(row.outputs[0],parseError)}
  else{assert.equal(outcome.type,'TaskCanceledException');assert.deepEqual(row.actions.find(x=>x.observedUnbounded)?.observedUnbounded,{bytes:c.size,decoded:0,oversize:false});assert.equal(of(row,'read-ended')[0].bufferedBytes,c.size)}
  let rejected;try{acceptance(c,row)}catch(e){rejected=e}
  assert.ok(rejected,'relaxed graph policy must fail the identical bounded contract')
  proof.push({id,control:'relaxed-policy',rejected:true,cause:{graphMaxBytes:2*1024*1024,observedPayloadBytes:c.size,decoded:!!c.terminator,releaseCount:1},contract:'predecode-byte-limit'})
  console.log(`PASS: compiled relaxed graph policy causes ${id} bound failure`)
 }
 const guardCase={id:'unrelated-input-fault-guard',mode:'valid-boundary'},guard=await execute(guardCase,'unrelated-input-fault')
 const outcome=lifecycle(guard);assert.equal(outcome.type,'NotSupportedException');assert.equal(of(guard,'input-oversize').length,0)
 assert.equal(of(guard,'unrelated-input-fault').length,1)
 let refused;try{acceptance({id:guardCase.id,mode:'oversize'},guard)}catch(e){refused=e}
 assert.ok(refused,'the identical oversize oracle must refuse unrelated faults')
 guards.push({id:guardCase.id,refusedAsOversize:true,type:outcome.type})
 console.log('PASS: unrelated fault refused as oversize evidence')
}finally{await writeFile(join(dir,'pipe-raw.json'),JSON.stringify(retained,null,2)+'\n')}
const files=['session.json','Generated.cs','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','symbols.json','declarations.json','authoring.json','pipe-cases.json','compilation.json','pipe-raw.json']
const hashes={};for(const file of files)hashes[file]=hash(await readFile(join(dir,file)))
for(const file of files.filter(x=>!x.startsWith('pipe-')))hashes['relaxed/'+file]=hash(await readFile(join(dir,'relaxed',file)))
const sourceHashes={}
async function source(path){
 if(sourceHashes[path])return
 const bytes=await readFile(path);sourceHashes[path]=hash(bytes)
 for(const match of bytes.toString().matchAll(/(?:from\s+|import\s*)['"]([^'"]+)['"]/g))if(match[1].startsWith('.'))await source(resolve(path,'..',match[1]))
}
for(const name of ['author.mjs','run.mjs','compile.mjs','test-runtime.mjs','verify.mjs','pipe.template.cs'])await source(resolve('examples/offline-session-bounded-input',name))
for(const path of ['examples/offline-session-owned-io/pipe.template.cs','examples/offline-leader-jsonrpc/runtime.template.cs'])await source(resolve(path))
await writeFile(join(dir,'pipe-proof.json'),JSON.stringify({base:'f62782acd06188f35ba4ccb4fd6c3517a261a956',baseTree:'1b5fe82c6d64bd7dfee934e7ff3afbdee0ae416a',limitBytes:limit,execution:'real compiled Linux pipes; graph maxBytes policy; bounded host framing before strict UTF-8 decode; synthetic application effects',proof,guards,hashes,sourceHashes:Object.fromEntries(Object.entries(sourceHashes).map(([path,value])=>[path.slice(resolve('.').length+1),value]))},null,2)+'\n')
