// Local investigation only: exercise the current authoring/runtime surface.
import assert from 'node:assert/strict'
import { readFile, writeFile, appendFile } from 'node:fs/promises'
import { resolve, join } from 'node:path'
import * as z from 'zod/v4'
import { createGraphTools } from '../../tools/nyrwvy-agent/graph-tools.mjs'
const root=resolve(process.argv[2]); const snapshot=join(root,'baseline.nyrec')
const tools=createGraphTools(z), context={sessionID:`handoff-probe-${process.pid}`,worktree:resolve('.')}
const evidence={baseline:[],limitations:[]}
async function exchange(name,args={}) { const raw=await tools[name].execute(args,context); await appendFile(join(root,'transcript.jsonl'),JSON.stringify({name,args,raw})+'\n'); return name==='close'?raw:JSON.parse(raw) }
async function call(name,args={}) {const r=await exchange(name,args); assert.notEqual(r.ok,false,JSON.stringify(r));return r.result??r}
async function publish(proposal) {assert.equal(proposal.review.readiness.kind,'semanticallyReady');await call('commit',{proposalId:proposal.proposalId})}
const primitive=primitive=>({kind:'primitive',primitive}), string=primitive('string'), unit=primitive('unit'), int=primitive('int64')
const generic=(generic,...args)=>({kind:'builtinGeneric',generic,arguments:args}), named=name=>({kind:'named',name}), cap=name=>({kind:'capability',name})
const result=t=>generic('result',t,string), optString=generic('option',string), state=named('State'), response=named('Response')
const p=name=>({kind:'parameter',name}), text=value=>({kind:'stringLiteral',value}), integer=n=>({kind:'int64Literal',value:String(n)})
const pattern=(slot='value',scopeDepth=0)=>({kind:'patternReference',slot,scopeDepth})
const field=(source,type,field)=>({kind:'fieldAccess',source,type,field})
const binary=(kind,left,right)=>({kind,left,right}), choose=(condition,whenTrue,whenFalse)=>({kind:'conditional',condition,whenTrue,whenFalse})
const ok=value=>({kind:'resultOk',value,errorType:string}), err=error=>({kind:'resultErr',error,okType:state})
const bind=(value,body)=>({kind:'bind',value,body})
const direct=(fn,values)=>({kind:'directCall',function:fn,arguments:Object.entries(values).map(([parameter,value])=>({parameter,value}))})
const invoke=(operation,values={})=>({kind:'capabilityInvoke',capabilityValue:p('wire'),capability:'Wire',operation,arguments:Object.entries(values).map(([parameter,value])=>({parameter,value}))})
const product=(a,b,duplicates)=>({kind:'productConstruct',type:'State',fields:Object.entries({a,b,duplicates}).map(([field,value])=>({field,value}))})
async function create(displayName,parameters,resultType,body,requiredEffects=['Transport']) {await publish(await call('create_function',{displayName,parameters:parameters.map(([displayName,type])=>({displayName,type})),resultType,body,requiredEffects}))}
try {
 await call('open',{mode:'create',snapshot})
 for(const [displayName,fields] of [['Response',[['id',string],['payload',string]]],['State',[['a',optString],['b',optString],['duplicates',int]]]]) await publish(await call('create_type',{displayName,typeKind:{kind:'product',fields:fields.map(([displayName,type])=>({displayName,type}))}}))
 await publish(await call('request',{op:'simulateCreateEffect',arguments:{displayName:'Transport'}}))
 await publish(await call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'Wire',operations:[
  ['open',[],result(unit)],['send',[['id',string]],unit],['read',[],result(response)],['wait',[['id',string]],result(string)],['close',[['handle',unit]],unit],
 ].map(([displayName,parameters,resultType])=>({displayName,parameters:parameters.map(([displayName,type])=>({displayName,type})),resultType,effect:'Transport'}))}}))
 const acc=name=>field(p('state'),'State',name), incoming=field(p('response'),'Response','payload')
 const update=name=>({kind:'matchOption',input:acc(name),someBody:product(acc('a'),acc('b'),binary('int64WrappingAdd',acc('duplicates'),integer(1))),noneBody:product(name==='a'?{kind:'optionSome',value:incoming}:acc('a'),name==='b'?{kind:'optionSome',value:incoming}:acc('b'),acc('duplicates'))})
 await create('route',[['state',state],['response',response]],state,choose(binary('valueEqual',field(p('response'),'Response','id'),text('a')),update('a'),choose(binary('valueEqual',field(p('response'),'Response','id'),text('b')),update('b'),p('state'))),[])
 await create('pump',[['wire',cap('Wire')],['state',state],['left',int]],result(state),choose(binary('valueEqual',p('left'),integer(0)),ok(p('state')),{kind:'matchResult',input:invoke('read'),okBody:direct('pump',{wire:p('wire'),state:direct('route',{state:p('state'),response:pattern()}),left:binary('int64WrappingSubtract',p('left'),integer(1))}),errBody:err(pattern('error'))}))
 const empty={kind:'optionNone',itemType:string}
 await create('body',[['wire',cap('Wire')],['count',int],['handle',unit]],result(state),bind(invoke('send',{id:text('a')}),bind(invoke('send',{id:text('b')}),direct('pump',{wire:p('wire'),state:product(empty,empty,integer(0)),left:p('count')}))))
 await create('run',[['wire',cap('Wire')],['count',int]],result(state),{kind:'useResource',acquire:invoke('open'),releaseCapability:p('wire'),capability:'Wire',releaseOperation:'close',body:'body',context:[{parameter:'wire',value:p('wire')},{parameter:'count',value:p('count')}]})
 // A finished receiver task supplies only its own outcome; another task stays pending.
 await create('receiver',[['wire',cap('Wire')],['item',unit]],result(string),{kind:'matchResult',input:invoke('read'),okBody:ok(field(pattern(),'Response','payload')),errBody:{kind:'resultErr',error:pattern('error'),okType:string}})
 await create('requestWaiter',[['wire',cap('Wire')],['id',string]],result(string),invoke('wait',{id:p('id')}))
 const start=(worker,item)=>({kind:'taskStart',scope:p('scope'),worker,item,context:[{parameter:'wire',value:p('wire')}]})
 const wait=(task,limit)=>({kind:'taskWait',tasks:{kind:'listConstruct',itemType:generic('task',result(string)),items:[task]},mode:'all',...(limit?{limit}: {})})
 await create('separateScope',[['wire',cap('Wire')],['scope',generic('taskScope')]],result(string),
  bind(start('requestWaiter',text('a')),bind(start('receiver',{kind:'unitLiteral'}),
   bind(wait(pattern()),bind(wait(pattern('value',2),{kind:'optionSome',value:integer(0)}),
    {kind:'matchOption',input:{kind:'listItemAt',list:pattern(),index:integer(0)},someBody:{kind:'matchOption',input:pattern(),someBody:ok(text('request unexpectedly finished')),noneBody:ok(text('receiver finished; request still pending'))},noneBody:ok(text('missing task'))}))))
 )
 await create('separateResource',[['wire',cap('Wire')],['handle',unit]],result(string),{kind:'taskScope',body:'separateScope',context:[{parameter:'wire',value:p('wire')}]})
 await create('separateRun',[['wire',cap('Wire')]],result(string),{kind:'useResource',acquire:invoke('open'),releaseCapability:p('wire'),capability:'Wire',releaseOperation:'close',body:'separateResource',context:[{parameter:'wire',value:p('wire')}]})
 // Current API does not expose a producer for an existing Task's outcome.
 const before=await readFile(snapshot)
 const denied=await exchange('create_function',{displayName:'completeExistingTask',parameters:[{displayName:'task',type:generic('task',result(string))}],resultType:unit,requiredEffects:[],body:{kind:'taskComplete',task:p('task'),value:ok(text('answer'))}})
 assert.equal(denied.ok,false); assert.match(JSON.stringify(denied.error),/Unknown expression kind|Unsupported expression kind|taskComplete/); assert.deepEqual(await readFile(snapshot),before)
 evidence.limitations.push({case:'external Task completion',error:denied.error})
 const boundary=await exchange('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'HostCompletion',operations:[{displayName:'complete',parameters:[{displayName:'task',type:generic('task',result(string))}],resultType:unit,effect:'Transport'}]}})
 assert.equal(boundary.ok,false);assert.match(JSON.stringify(boundary.error),/TaskTypeAtCapabilityBoundary/); assert.deepEqual(await readFile(snapshot),before)
 evidence.limitations.push({case:'Task handle passed to host for completion',error:boundary.error})
 await call('close');await call('open',{mode:'open',snapshot})
 const runString=value=>({kind:'string',value}), runUnit={kind:'unit'}, runResult=(value,isOk=true,okType=unit)=>okType===string?({kind:'result',okKind:'string',errorKind:'string',isOk,value:value.value}):({kind:'result',okType,errorType:string,isOk,value})
 const taggedResponse=(id,payload)=>({kind:'product',type:'Response',fields:[{field:'id',value:runString(id)},{field:'payload',value:runString(payload)}]})
 const returned=value=>({kind:'return',value})
 for(const [id,reads,count,expected] of [
  ['reverse',[returned(runResult(taggedResponse('b','B'),true,response)),returned(runResult(taggedResponse('a','A'),true,response))],2,[['a','A'],['b','B'],['duplicates','0']]],
  ['duplicate',[returned(runResult(taggedResponse('b','B'),true,response)),returned(runResult(taggedResponse('b','replacement'),true,response)),returned(runResult(taggedResponse('a','A'),true,response))],3,[['a','A'],['b','B'],['duplicates','1']]],
  ['receiver-host-exception',[{kind:'throw',message:'receiver failed'}],2,null],
 ]) {
  const fake={kind:'fakeCapability',capability:'Wire',operations:[{operation:'open',responses:[returned(runResult(runUnit))]},{operation:'send',responses:[returned(runUnit),returned(runUnit)]},{operation:'read',responses:reads},{operation:'close',responses:[returned(runUnit)]}]}
  const r=await exchange('request',{op:'runPureByName',arguments:{function:'run',arguments:[fake,{kind:'int64',value:String(count)}]}})
  const calls=expected?r.result.calls:r.error.calls
  assert.equal(calls.filter(c=>c.operation==='open').length,1);assert.equal(calls.filter(c=>c.operation==='close').length,1);assert.equal(calls.at(-1).operation,'close')
  if(expected) {assert.equal(r.ok,true,JSON.stringify(r)); const fields=r.result.actual.value.fields; for(const [name,value] of expected) {const actual=fields.find(f=>f.field===name).value;assert.equal(actual.value,value)}}
  else {assert.equal(r.ok,false);assert.equal(r.error.code,'RunRuntimeFailure')}
  evidence.baseline.push({case:id,result:r});console.log(`PASS: baseline ${id}; resource closed exactly once`)
 }
 const separateFake={kind:'fakeCapability',capability:'Wire',operations:[{operation:'open',responses:[returned(runResult(runUnit))]},{operation:'read',responses:[returned(runResult(taggedResponse('a','A'),true,response))]},{operation:'wait',responses:[{kind:'awaitCancellation',value:runResult(runString('cancelled'),false,string)}]},{operation:'close',responses:[returned(runUnit)]}]}
 const separate=await call('request',{op:'runPureByName',arguments:{function:'separateRun',arguments:[separateFake]}})
 assert.equal(separate.actual.value,'receiver finished; request still pending')
 assert.equal(separate.calls.filter(c=>c.operation==='close').length,1)
 assert.equal(separate.calls.at(-1).operation,'close')
 evidence.baseline.push({case:'separate receiver completion leaves request pending; scope cancels and joins it',result:separate})
 console.log('PASS: receiver finished; request remains pending; scope cancellation/join and one release confirmed')
 await writeFile(join(root,'evidence.json'),JSON.stringify(evidence,null,2));console.log('PASS: current API limits reproduced; baseline author/review/save/reopen executed')
} finally {await exchange('close')}
