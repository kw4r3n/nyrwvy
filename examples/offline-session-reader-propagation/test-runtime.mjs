import assert from 'node:assert/strict'
import {readFile,writeFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
import {createHash} from 'node:crypto'
import {arg} from '../offline-leader-jsonrpc/cases.mjs'
import {maxFrameBytes} from './author.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const ping=id=>JSON.stringify({jsonrpc:'2.0',id,method:'ping'})
const command=(id,op,label,extra={})=>JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:{op,label,...extra}}})
const of=(row,op)=>row.events.filter(x=>x.operation===op),phase=(row,p)=>of(row,'step').filter(x=>arg(x,'operation')===p)
const cases=[
 {id:'foreground-oversize',mode:'foreground',fault:'oversize',witness:true},
 {id:'foreground-invalid-utf8',mode:'foreground',fault:'utf8',witness:true},
 {id:'foreground-eof',mode:'foreground',eof:true},
 {id:'foreground-healthy',mode:'foreground',healthy:true,witness:true},
 {id:'idle-oversize',fault:'oversize'},
 {id:'idle-invalid-utf8',fault:'utf8'},
 {id:'retained-input-cancel',cancel:true},
 {id:'fragmented-ping-eof',healthy:true},
]
await writeFile(join(dir,'pipe-cases.json'),JSON.stringify(cases,null,2)+'\n')
const rows=[],proof=[],guards=[]
async function normalizedHost(root){
 const declarations=JSON.parse(await readFile(join(root,'declarations.json'),'utf8')),symbols=JSON.parse(await readFile(join(root,'symbols.json'),'utf8')),labels=new Map()
 function walk(value,path=''){
  if(Array.isArray(value)){for(const item of value)walk(item,path);return}
  if(!value||typeof value!=='object')return
  if(value.displayName){path+=`/${value.displayName}`;if(value.id)labels.set(value.id,path)}
  for(const [key,item] of Object.entries(value))if(item&&typeof item==='object')walk(item,path+'/'+key)
 }
 walk(declarations)
 let source=await readFile(join(root,'Program.cs'),'utf8')
 for(const symbol of Object.values(symbols).filter(Array.isArray).flat().sort((a,b)=>b.name.length-a.name.length)){
  if(source.includes(symbol.name)){assert.ok(labels.has(symbol.id),symbol.id);source=source.split(symbol.name).join('@@'+labels.get(symbol.id)+'@@')}
 }
 return source
}
const host=await normalizedHost(dir)
assert.equal(host,await normalizedHost(join(dir,'deferred')),'supported and causal graphs use the identical host after declared-symbol normalization')
async function execute(c,{deferred=false,control=''}={}){
 const gateFile=join(dir,'commit-release'),fixture=join(dir,'scenario.json')
 await rm(gateFile,{force:true})
 const scenario={kind:'transport',patch:'diff',providers:c.mode==='foreground'?{1:'complete',...(c.witness?{2:'hold'}:{})}:{},...(c.mode==='foreground'?{commitGateFile:gateFile}:{}),...c}
 await writeFile(fixture,JSON.stringify(scenario))
 const child=spawn('dotnet',[join(dir,...(deferred?['deferred','Runtime.dll']:['Runtime.dll'])),fixture,control],{stdio:['pipe','pipe','pipe']})
 const events=[],outputs=[],actions=[],times=[],waiters=new Set();let failure,exitCode,signal
 const wake=()=>{for(const waiter of waiters)waiter()}
 for(const [stream,target] of [[child.stdout,outputs],[child.stderr,events]])createInterface({input:stream}).on('line',line=>{try{target.push(target===events?JSON.parse(line):line);if(target===events)times.push(performance.now())}catch(e){failure=e}wake()})
 child.on('error',e=>{failure=e;wake()});child.stdin.on('error',e=>{failure=e;wake()})
 const ended=new Promise(resolve=>child.once('close',(code,sig)=>{exitCode=code;signal=sig;wake();resolve()}))
 const timer=setTimeout(()=>{failure=new Error('pipe probe deadline');child.kill('SIGKILL');wake()},20000)
 const until=async predicate=>{
  if(predicate())return
  await new Promise((resolve,reject)=>{const check=()=>{if(failure||exitCode!==undefined){waiters.delete(check);reject(failure??new Error('child ended before observation'))}else if(predicate()){waiters.delete(check);resolve()}};waiters.add(check);check()})
 }
 const write=async bytes=>{actions.push({writeHex:bytes.toString('hex')});await new Promise((resolve,reject)=>child.stdin.write(bytes,e=>e?reject(e):resolve()))}
 const eof=()=>{actions.push({eof:true});child.stdin.end()}
 const receipt=id=>until(()=>outputs.some(x=>JSON.parse(x).id===id))
 const send=async line=>{await write(Buffer.from(line+'\n'));await receipt(JSON.parse(line).id)}
 const release=async()=>{actions.push({releaseProtectedGate:true});await writeFile(gateFile,'release\n')}
 const index=pred=>events.findIndex(pred)
 const time=pred=>times[index(pred)]
 try{
  await until(()=>events.some(x=>x.operation==='read-start'))
  if(c.mode==='foreground'){
   await send(command(1,'begin','member',{target:1,mode:'spawn'}));await send(command(2,'gate','provider1'))
   await send(command(3,'gate','snapshot1'));await send(command(4,'gate','release1'));await send(command(5,'wait','settled',{target:1}))
   if(c.witness){await send(command(7,'begin','witness',{target:2,mode:'spawn'}));await send(command(8,'gate','provider2'))}
   await write(Buffer.from(command(6,'integrate','held',{target:1})+'\n'))
   await until(()=>events.some(x=>x.operation==='step'&&arg(x,'operation')==='commit'))
  }
  if(c.fault){
   await write(c.fault==='oversize'?Buffer.alloc(maxFrameBytes+1,65):Buffer.from([0xff,10]))
   await until(()=>events.some(x=>x.operation==='read-ended'&&x.bufferedBytes===(c.fault==='oversize'?maxFrameBytes:1)))
   if(c.mode==='foreground'){
    if(!deferred)await until(()=>events.some(x=>x.operation==='provider-ended'&&x.id===2&&x.cancelled))
    await new Promise(r=>setTimeout(r,350))
    actions.push({heldObservation:{ms:350,witnessCancelled:events.some(x=>x.operation==='provider-ended'&&x.id===2&&x.cancelled),outcome:events.some(x=>x.operation==='outcome'),head:events.some(x=>x.operation==='step'&&arg(x,'operation')==='head'),release:events.some(x=>x.operation==='close')}})
    await release()
   }
  }else if(c.cancel){
   await write(Buffer.from([0xe2,0x82]));await until(()=>events.some(x=>x.operation==='input-wait'&&x.bufferedBytes===2))
   actions.push({signal:'SIGTERM'});child.kill('SIGTERM')
  }else if(control==='unrelated-input-fault')await write(Buffer.from(ping('guard')+'\n'))
  else if(c.mode==='foreground'){
   if(c.eof)eof()
   else await write(Buffer.from(ping('after')+'\n'))
   await new Promise(r=>setTimeout(r,350))
   actions.push({heldObservation:{ms:350,witnessCancelled:events.some(x=>x.operation==='provider-ended'&&x.id===2&&x.cancelled),outcome:events.some(x=>x.operation==='outcome'),head:events.some(x=>x.operation==='step'&&arg(x,'operation')==='head'),release:events.some(x=>x.operation==='close')}})
   await release();await receipt(6)
   if(!c.eof){await receipt('after');eof()}
  }else{
   const bytes=Buffer.from(ping('雪😀')+'\n'),cut=bytes.indexOf(Buffer.from('😀'))+2
   await write(bytes.subarray(0,cut));await until(()=>events.some(x=>x.operation==='input-wait'&&x.bufferedBytes===cut))
   assert.equal(outputs.length,0);await write(bytes.subarray(cut));await receipt('雪😀');eof()
  }
  await ended;if(failure)throw failure
  const readEnd=time(x=>x.operation==='read-ended'&&x.bufferedBytes===(c.fault==='oversize'?maxFrameBytes:1))
  const cancelTime=time(x=>x.operation==='provider-ended'&&x.id===2&&x.cancelled)
  const row={id:c.id,deferred,control,scenario:{...scenario,...(scenario.commitGateFile?{commitGateFile:'<peer-gate-file>'}:{})},actions,events,outputs,exitCode,signal,measurements:{...(Number.isFinite(cancelTime-readEnd)?{readerEndToWitnessCancelMs:cancelTime-readEnd}:{}),...(Number.isFinite(readEnd)?{readerEndToCloseMs:performance.now()-readEnd}:{})}}
  rows.push(row);return row
 }finally{clearTimeout(timer);if(exitCode===undefined){child.kill('SIGKILL');await ended}}
}
function lifecycle(row){
 const out=of(row,'outcome')[0];assert.ok(out);assert.equal(row.exitCode,0);assert.equal(row.signal,null)
 assert.equal(out.Opened,1);assert.equal(out.Closed,1);assert.equal(out.Active,0)
 for(const op of ['outcome','close','input-fd-closed','transport-close','process-terminal'])assert.equal(of(row,op).length,1,op)
 assert.equal(of(row,'read-start').length,of(row,'read-ended').length);assert.equal(of(row,'transport-close')[0].activeReads,0)
 const i=op=>row.events.findIndex(x=>x.operation===op)
 assert.ok(i('close')<i('transport-close')&&i('transport-close')<i('process-terminal'))
 assert.ok(row.events.findLastIndex(x=>x.operation==='read-ended')<i('input-fd-closed'))
 for(const id of row.scenario.mode==='foreground'?(row.scenario.witness?[1,2]:[1]):[]){
  assert.equal(of(row,'memberClose').filter(x=>arg(x,'id')===String(id)).length,1)
  assert.ok(row.events.findIndex(x=>x.operation==='memberClose'&&arg(x,'id')===String(id))<i('close'))
 }
 return out
}
function acceptance(c,row){
 const out=lifecycle(row)
 if(c.fault){
  assert.equal(out.ok,false);assert.equal(out.type,c.fault==='oversize'?'InvalidDataException':'DecoderFallbackException')
  assert.equal(out.readerFaultIdentity,true);assert.equal(out.readerFaultStack,true)
  assert.equal(out.TerminalWritten,false);assert.ok(!row.outputs.some(x=>JSON.parse(x).method==='session/terminal'))
  assert.ok(!row.actions.some(x=>x.eof||x.signal),'reader failure propagation requires neither EOF nor external cancellation')
  if(c.mode==='foreground'){
   assert.deepEqual(row.actions.find(x=>x.heldObservation).heldObservation,{ms:350,witnessCancelled:true,outcome:false,head:false,release:false})
   assert.ok(row.measurements.readerEndToWitnessCancelMs>=0&&row.measurements.readerEndToWitnessCancelMs<1000,'reader fault promptly cancels linked provider before protected gate release')
  }
 }else if(c.cancel){assert.equal(out.ok,false);assert.equal(out.type,'TaskCanceledException');assert.equal(out.readerFaultIdentity,true);assert.equal(out.TerminalWritten,false)}
 else{assert.equal(out.ok,true);assert.equal(out.isOk,true);assert.equal(out.TerminalWritten,true);assert.equal(JSON.parse(row.outputs.at(-1)).method,'session/terminal')}
 if(c.mode==='foreground'){
  for(const p of ['check','apply','commit','head','save'])assert.equal(phase(row,p).length,1,p)
  assert.equal(arg(phase(row,'save')[0],'patch'),'integrated-head')
  assert.ok(of(row,'protected-step').every(x=>x.cancelled===false),'every determined mutation sees protected cancellation token')
  assert.equal(of(row,'external-gate-released').length,1)
  const releaseIndex=row.events.findIndex(x=>x.operation==='external-gate-released')
  assert.ok(releaseIndex<row.events.indexOf(phase(row,'head')[0]))
  if(!c.fault)assert.deepEqual(row.actions.find(x=>x.heldObservation).heldObservation,{ms:350,witnessCancelled:false,outcome:false,head:false,release:false})
 }
 if(c.fault==='oversize')assert.equal(of(row,'input-oversize').length,1)
 if(c.fault==='utf8')assert.equal(of(row,'input-oversize').length,0)
 return {id:c.id,readerFault:c.fault??null,releaseCount:1,protectedTail:c.mode==='foreground',promptCancellation:!!(c.fault&&c.witness)}
}
try{
 for(const c of cases){const row=await execute(c);proof.push(acceptance(c,row));console.log(`PASS: owned reader propagation ${c.id}`)}
 for(const id of ['foreground-oversize','foreground-invalid-utf8']){
  const c=cases.find(x=>x.id===id),row=await execute(c,{deferred:true}),out=lifecycle(row)
  assert.deepEqual(row.actions.find(x=>x.heldObservation).heldObservation,{ms:350,witnessCancelled:false,outcome:false,head:false,release:false})
  assert.equal(out.type,c.fault==='oversize'?'InvalidDataException':'DecoderFallbackException');assert.equal(out.readerFaultIdentity,true)
  for(const p of ['commit','head','save'])assert.equal(phase(row,p).length,1)
  let refused;try{acceptance(c,row)}catch(e){refused=e}
  assert.ok(refused,'identical prompt-cancellation contract rejects separately authored old graph')
  proof.push({id,control:'deferred-graph',rejected:true,readerFaultIdentity:true,protectedTail:true,releaseCount:1})
  console.log(`PASS: old graph causally defers ${id} cancellation until gate release`)
 }
 const row=await execute({id:'unrelated-input-fault-guard'},{control:'unrelated-input-fault'}),out=lifecycle(row)
 assert.equal(out.type,'NotSupportedException');assert.equal(out.readerFaultIdentity,true);assert.equal(of(row,'input-oversize').length,0)
 let refused;try{acceptance({fault:'oversize'},row)}catch(e){refused=e}
 assert.ok(refused);guards.push({id:row.id,type:out.type,refusedAsOversize:true})
 console.log('PASS: unrelated host fault is preserved and refused as oversize evidence')
}finally{await writeFile(join(dir,'pipe-raw.json'),JSON.stringify(rows,null,2)+'\n')}
const hashes={},sourceHashes={}
const files=['session.json','Generated.cs','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','symbols.json','declarations.json','authoring.json','compilation.json']
for(const prefix of ['','deferred/'])for(const file of files)hashes[prefix+file]=hash(await readFile(join(dir,prefix+file)))
for(const file of ['pipe-cases.json','pipe-raw.json'])hashes[file]=hash(await readFile(join(dir,file)))
async function source(path){
 if(sourceHashes[path])return
 const bytes=await readFile(path);sourceHashes[path]=hash(bytes)
 for(const m of bytes.toString().matchAll(/(?:from\s+|import\s*)['"]([^'"]+)['"]/g))if(m[1].startsWith('.'))await source(resolve(path,'..',m[1]))
}
for(const file of ['author.mjs','run.mjs','compile.mjs','test-runtime.mjs','verify.mjs','probe-restart.mjs'])await source(resolve('examples/offline-session-reader-propagation',file))
for(const file of ['examples/offline-session-owned-io/pipe.template.cs','examples/offline-session-bounded-input/pipe.template.cs','examples/offline-leader-jsonrpc/runtime.template.cs'])await source(resolve(file))
await writeFile(join(dir,'pipe-proof.json'),JSON.stringify({base:'43507a5aee1aa0f706ec4b86e19392936fdcd94a',baseTree:'a5ac4707f764dcd2219e7d16badb57a29f5b18c3',execution:'real Linux pipes; ordinary graph supervision; externally held synthetic effects',identicalNormalizedHostHash:hash(host),proof,guards,hashes,sourceHashes:Object.fromEntries(Object.entries(sourceHashes).map(([p,h])=>[p.slice(resolve('.').length+1),h]))},null,2)+'\n')
