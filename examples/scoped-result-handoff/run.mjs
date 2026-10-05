// Two independent request workers, one receiver, and a coordinator; policies stay in the graph.
import assert from 'node:assert/strict'
import { mkdir,mkdtemp,readFile,writeFile,appendFile,readdir } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join,resolve } from 'node:path'
import * as z from 'zod/v4'
import { createGraphTools } from '../../tools/nyrwvy-agent/graph-tools.mjs'
const replay=process.argv[2]==='--replay'
if(replay&&!process.argv[3])throw new Error('Usage: run.mjs --replay <bundle-directory>')
const directory=process.argv[replay?3:2]?resolve(process.argv[replay?3:2]):await mkdtemp(join(tmpdir(),'nyrwvy-handoff-'))
await mkdir(directory,{recursive:true});if(!replay)assert.deepEqual(await readdir(directory),[])
const snapshot=join(directory,'broker.json'),tools=createGraphTools(z),context={sessionID:`broker-${process.pid}`,worktree:resolve('.')}
async function exchange(name,args={}) {const raw=await tools[name].execute(args,context);await appendFile(join(directory,'transcript.jsonl'),JSON.stringify({name,args,raw})+'\n');return name==='close'?raw:JSON.parse(raw)}
async function call(name,args={}) {const r=await exchange(name,args);assert.notEqual(r.ok,false,JSON.stringify(r));return r.result??r}
async function publish(proposal) {assert.equal(proposal.review.readiness.kind,'semanticallyReady');await call('commit',{proposalId:proposal.proposalId})}
const primitive=primitive=>({kind:'primitive',primitive}), string=primitive('string'), unit=primitive('unit'), int=primitive('int64'),bool=primitive('bool')
const generic=(generic,...args)=>({kind:'builtinGeneric',generic,arguments:args}), named=name=>({kind:'named',name}), cap=name=>({kind:'capability',name})
const result=t=>generic('result',t,string), outcome=result(string), cell=generic('oneShot',outcome), task=generic('task',outcome), scope=generic('taskScope'), states=generic('list',generic('option',outcome))
const p=name=>({kind:'parameter',name}), text=value=>({kind:'stringLiteral',value}), integer=n=>({kind:'int64Literal',value:String(n)}), pattern=(slot='value',scopeDepth=0)=>({kind:'patternReference',slot,scopeDepth})
const field=(source,type,field)=>({kind:'fieldAccess',source,type,field}), binary=(kind,left,right)=>({kind,left,right}), choose=(condition,whenTrue,whenFalse)=>({kind:'conditional',condition,whenTrue,whenFalse})
const concat=(...values)=>values.reduce((left,right)=>binary('stringConcat',left,right)), ok=value=>({kind:'resultOk',value,errorType:string}), err=error=>({kind:'resultErr',error,okType:string}), bind=(value,body)=>({kind:'bind',value,body})
const direct=(fn,values)=>({kind:'directCall',function:fn,arguments:Object.entries(values).map(([parameter,value])=>({parameter,value}))})
const invoke=(operation,values={})=>({kind:'capabilityInvoke',capabilityValue:p('wire'),capability:'Wire',operation,arguments:Object.entries(values).map(([parameter,value])=>({parameter,value}))})
const list=items=>({kind:'listConstruct',itemType:task,items}), matchOption=(input,someBody,noneBody)=>({kind:'matchOption',input,someBody,noneBody}), cancellation={kind:'cancellationRequested'}, limits={limitA:int,limitB:int}
const complete=(cell,value)=>({kind:'oneShotComplete',cell,value}), taskWait=(tasks,limit)=>({kind:'taskWait',tasks,mode:'all',...(limit?{limit}: {})}), poll={kind:'optionSome',value:integer(0)}
async function create(displayName,parameters,resultType,body,requiredEffects=['Transport']) {await publish(await call('create_function',{displayName,parameters:Object.entries(parameters).map(([displayName,type])=>({displayName,type})),resultType,body,requiredEffects}))}
async function author() {
 await call('open',{mode:'create',snapshot})
 await publish(await call('create_type',{displayName:'Response',typeKind:{kind:'product',fields:[{displayName:'id',type:string},{displayName:'payload',type:string}]}}))
 await publish(await call('request',{op:'simulateCreateEffect',arguments:{displayName:'Transport'}}))
 for(const [displayName,operations] of [['Wire',[
  ['open',{},result(unit)],['send',{id:string},unit],['read',{},result(named('Response'))],['note',{message:string},unit],['save',{report:string},unit],['close',{handle:unit},unit],
 ]],['Control',[['pause',{},unit]]]]) await publish(await call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName,operations:operations.map(([displayName,parameters,resultType])=>({displayName,parameters:Object.entries(parameters).map(([displayName,type])=>({displayName,type})),resultType,effect:'Transport'}))}}))
 const t={kind:'typeParameter',name:'T'}
 await create('newCell',{scope,witness:t},generic('oneShot',t),{kind:'oneShotCreate',scope:p('scope'),valueType:t},[])
 await create('publishValue',{cell:generic('oneShot',t),value:t},bool,complete(p('cell'),p('value')),[])
 await create('deliver',{a:cell,b:cell,response:named('Response')},bool,
  choose(binary('stringEqual',field(p('response'),'Response','id'),text('a')),direct('publishValue',{cell:p('a'),value:ok(field(p('response'),'Response','payload'))}),choose(binary('stringEqual',field(p('response'),'Response','id'),text('b')),direct('publishValue',{cell:p('b'),value:ok(field(p('response'),'Response','payload'))}),{kind:'boolLiteral',value:false})),[])
 await create('requestWorker',{cell,id:string,limit:int},outcome,
  bind({kind:'oneShotWait',cell:p('cell'),limit:{kind:'optionSome',value:p('limit')}},choose(cancellation,err(text('cancelled')),matchOption(pattern(),pattern(),err(text('timeout'))))),[])
 await create('receiver',{wire:cap('Wire'),a:cell,b:cell,item:unit},outcome,
  choose(cancellation,err(text('cancelled')),{kind:'matchResult',input:invoke('read'),okBody:choose(cancellation,err(text('cancelled')),
   bind(direct('deliver',{a:p('a'),b:p('b'),response:pattern()}),bind(choose(pattern(),{kind:'unitLiteral'},invoke('note',{message:concat(text('duplicate or unknown '),field(pattern('value',1),'Response','id'))})),direct('receiver',{wire:p('wire'),a:p('a'),b:p('b'),item:p('item')})))),
   errBody:choose(cancellation,err(text('cancelled')),bind(complete(p('a'),err(pattern('error'))),bind(complete(p('b'),err(pattern('error',1))),err(pattern('error',2)))))}))
 await create('at',{states,index:int},generic('option',outcome),matchOption({kind:'listItemAt',list:p('states'),index:p('index')},pattern(),{kind:'optionNone',itemType:outcome}),[])
 await create('format',{value:generic('option',outcome)},string,matchOption(p('value'),{kind:'matchResult',input:pattern(),okBody:pattern(),errBody:pattern('error')},text('pending')),[])
 const stateAt=(source,index)=>direct('at',{states:source,index:integer(index)}), present=input=>matchOption(input,{kind:'boolLiteral',value:true},{kind:'boolLiteral',value:false})
 const allTasks=list([p('aTask'),p('bTask'),p('readerTask')])
 const pending=source=>[p('aTask'),p('bTask'),p('readerTask')].map((task,index)=>matchOption(stateAt(source,index),list([]),list([task]))).reduce((left,right)=>binary('listConcat',left,right))
 const report=source=>concat(text('a='),direct('format',{value:stateAt(source,0)}),text(';b='),direct('format',{value:stateAt(source,1)}))
 const finish=bind({kind:'taskCancel',task:p('readerTask')},bind(taskWait(list([p('readerTask')])),choose(cancellation,err(text('cancelled')),
  bind(invoke('save',{report:report(pattern('value',2))}),ok(report(pattern('value',3)))))))
 await create('watch',{wire:cap('Wire'),aTask:task,bTask:task,readerTask:task},outcome,
  bind(taskWait(allTasks,poll),choose(binary('boolAnd',present(stateAt(pattern(),0)),present(stateAt(pattern(),1))),finish,
   bind({kind:'taskWait',tasks:pending(pattern()),mode:'any'},direct('watch',{wire:p('wire'),aTask:p('aTask'),bTask:p('bTask'),readerTask:p('readerTask')})))))
 const startRequest=(name,limit)=>({kind:'taskStart',scope:p('scope'),worker:'requestWorker',item:text(name),context:[{parameter:'cell',value:p(name)},{parameter:'limit',value:p(limit)}]})
 await create('tasks',{wire:cap('Wire'),scope,a:cell,b:cell,...limits},outcome,
  bind(startRequest('a','limitA'),bind(startRequest('b','limitB'),bind({kind:'taskStart',scope:p('scope'),worker:'receiver',item:{kind:'unitLiteral'},context:['wire','a','b'].map(parameter=>({parameter,value:p(parameter)}))},direct('watch',{wire:p('wire'),aTask:pattern('value',2),bTask:pattern('value',1),readerTask:pattern()})))))
 await create('setup',{wire:cap('Wire'),scope,...limits},outcome,
  bind(direct('newCell',{scope:p('scope'),witness:ok(text(''))}),bind(direct('newCell',{scope:p('scope'),witness:ok(text(''))}),direct('tasks',{wire:p('wire'),scope:p('scope'),a:pattern('value',1),b:pattern(),limitA:p('limitA'),limitB:p('limitB')}))))
 await create('resourceBody',{wire:cap('Wire'),...limits,handle:unit},outcome,
  bind(invoke('send',{id:text('a')}),bind(invoke('send',{id:text('b')}),{kind:'taskScope',body:'setup',context:['wire','limitA','limitB'].map(parameter=>({parameter,value:p(parameter)}))})))
 await create('runBroker',{wire:cap('Wire'),...limits},outcome,{kind:'useResource',acquire:invoke('open'),releaseCapability:p('wire'),capability:'Wire',releaseOperation:'close',body:'resourceBody',context:['wire','limitA','limitB'].map(parameter=>({parameter,value:p(parameter)}))})
 await create('brokerWorker',{wire:cap('Wire'),item:unit},outcome,direct('runBroker',{wire:p('wire'),limitA:integer(50),limitB:integer(50)}))
 await create('stopBroker',{control:cap('Control'),task},outcome,bind({kind:'capabilityInvoke',capabilityValue:p('control'),capability:'Control',operation:'pause',arguments:[]},bind({kind:'taskCancel',task:p('task')},ok(text('cancel sent')))))
 await create('outerBody',{wire:cap('Wire'),control:cap('Control'),scope},outcome,
  bind({kind:'taskStart',scope:p('scope'),worker:'brokerWorker',item:{kind:'unitLiteral'},context:[{parameter:'wire',value:p('wire')}]},bind({kind:'taskStart',scope:p('scope'),worker:'stopBroker',item:pattern(),context:[{parameter:'control',value:p('control')}]},
   bind(taskWait(list([pattern('value',1)])),matchOption(stateAt(pattern(),0),pattern(),err(text('broker still pending')))))))
 await create('runCancelled',{wire:cap('Wire'),control:cap('Control')},outcome,{kind:'taskScope',body:'outerBody',context:['wire','control'].map(parameter=>({parameter,value:p(parameter)}))})
 const artifact=await call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:(await call('inspect',{view:'declarations'})).capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:(await call('inspect',{view:'declarations'})).capabilities.map(c=>c.id)}}})
 await writeFile(join(directory,'Generated.cs'),artifact.source);await writeFile(join(directory,'symbols.json'),JSON.stringify(artifact.symbols,null,2))
 await call('close');await call('open',{mode:'open',snapshot})
}
const value=value=>({kind:'string',value}),u={kind:'unit'},returned=value=>({kind:'return',value})
const nestedResult=(value,isOk=true,okType=unit)=>({kind:'result',okType,errorType:string,isOk,value}),flatResult=(value,isOk=true)=>({kind:'result',okKind:'string',errorKind:'string',isOk,value})
const response=(id,payload)=>returned(nestedResult({kind:'product',type:'Response',fields:[{field:'id',value:value(id)},{field:'payload',value:value(payload)}]},true,named('Response')))
const awaiting={kind:'awaitCancellation',value:nestedResult(value('cancelled'),false,named('Response'))}
function wire(reads,notes=0) {return {kind:'fakeCapability',capability:'Wire',operations:[{operation:'open',responses:[returned(nestedResult(u))]},{operation:'send',responses:[returned(u),returned(u)]},{operation:'read',responses:reads},{operation:'note',responses:Array.from({length:notes},()=>returned(u))},{operation:'save',responses:[returned(u)]},{operation:'close',responses:[returned(u)]}]}}
async function runCases() {
 let cases
 if(replay) cases=JSON.parse(await readFile(join(directory,'cases.json'),'utf8'))
 else {
 cases=[
  {id:'reverse',reads:[response('b','B'),response('a','A'),awaiting],expected:flatResult('a=A;b=B')},
  {id:'duplicate',reads:[response('b','B'),response('b','replacement'),response('a','A'),awaiting],notes:1,expected:flatResult('a=A;b=B')},
  {id:'one-timeout',reads:[response('b','B'),awaiting],expected:flatResult('a=timeout;b=B')},
  {id:'receiver-expected-error',reads:[returned(nestedResult(value('receive failed'),false,named('Response')))],expected:flatResult('a=receive failed;b=receive failed')},
  {id:'receiver-host-exception',reads:[{kind:'throw',message:'receiver failed'}]},
  {id:'parent-cancel',reads:[awaiting],cancel:true,expected:flatResult('cancelled',false)},
 ].map(({reads,notes,cancel,...c})=>({...c,op:'runPureByName',function:cancel?'runCancelled':'runBroker',arguments:cancel?[wire(reads,notes),{kind:'fakeCapability',capability:'Control',operations:[{operation:'pause',responses:[{...returned(u),delayMillis:'1'}]}]}]:[wire(reads,notes),{kind:'int64',value:'3'},{kind:'int64',value:'10'}]}))
 await writeFile(join(directory,'cases.json'),JSON.stringify(cases,null,2))
 }
 const evidence=[]
 for(const c of cases) {
  const {id,op,...args}=c,r=await exchange('request',{op,arguments:args})
  if(c.expected) {assert.equal(r.ok,true,JSON.stringify(r));assert.equal(r.result.matches,true,JSON.stringify(r))}
  else {assert.equal(r.ok,false);assert.equal(r.error.code,'RunRuntimeFailure')}
  const calls=c.expected?r.result.calls:r.error.calls
  assert.equal(calls.filter(c=>c.operation==='open').length,1,id);assert.equal(calls.filter(c=>c.operation==='close').length,1,id);assert.equal(calls.at(-1).operation,'close',id)
  assert.equal(calls.filter(c=>c.operation==='save').length,['receiver-host-exception','parent-cancel'].includes(id)?0:1,id)
  if(id==='duplicate')assert.equal(calls.filter(c=>c.operation==='note').length,1)
  evidence.push({id,response:r});console.log(`PASS: ${id}; tasks settled and close exactly once; cancellation policy checked`)
 }
 await writeFile(join(directory,'evidence.json'),JSON.stringify(evidence,null,2))
}
try {if(!replay)await author();else await call('open',{mode:'open',snapshot});await runCases();console.log(`PASS: independent receiver/request broker. ${directory}`)} finally {await exchange('close')}
