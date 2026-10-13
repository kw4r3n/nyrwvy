// Program policy composed from existing recipes; no runtime cancellation state.
import {cap,string,unit,bool,gen,scope,task,result,p,text,unitValue,pat,bind,ok,err,direct,cond,invoke} from '../dynamic-worker-collector/lib.mjs'
const host=cap('Probe'),R=result(string),T=task(R),exception=gen('exception')
const ctx=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
const io=(operation,values={})=>invoke(p('host'),'Probe',operation,values)
const catchWith=(body,values,handler)=>({kind:'exceptionCatch',body,bodyContext:ctx(values),handler,handlerContext:[]})
export async function author(s){
 await s.call('open',{mode:'create',snapshot:s.snapshot})
 const publish=async(op,arguments_)=>s.publish(await s.call('request',{op,arguments:arguments_}))
 await publish('simulateCreateEffect',{displayName:'Io'})
 await publish('simulateCreateCapabilityByName',{displayName:'Probe',operations:[
  ['work',{name:string},R],['cleanup',{},unit],['open',{},result(unit)],['close',{handle:unit},unit]
 ].map(([displayName,parameters,resultType])=>({displayName,parameters:Object.entries(parameters).map(([displayName,type])=>({displayName,type})),resultType,effect:'Io'}))})
 const create=(name,parameters,body)=>s.create(name,parameters,R,body)
 await create('protectedWork',{host,name:string,item:unit},io('work',{name:p('name')}))
 await create('rawWorker',{host,name:string},direct('protectedWork',{host:p('host'),name:p('name'),item:unitValue}))
 const predicate={kind:'exceptionIsCancellation',value:p('exception')}
 for(const [name,condition] of [['cancelHandler',predicate],['invertedHandler',{kind:'boolNot',value:predicate}]])
  await s.create(name,{exception},R,cond(condition,err(text('stopped-by-program')),{kind:'exceptionRethrow',value:p('exception'),resultType:R}),[])
 for(const [name,handler] of [['adaptedWorker','cancelHandler'],['invertedWorker','invertedHandler']])
  await create(name,{host,name:string},catchWith('protectedWork',{host:p('host'),name:p('name')},handler))
 // Cleanup is after the adapter and is deliberately outside its exception catch.
 await create('cleanWorker',{host,name:string},bind(direct('adaptedWorker',{host:p('host'),name:p('name')}),bind(io('cleanup'),pat('value',1))))
 for(const worker of ['rawWorker','adaptedWorker','invertedWorker','cleanWorker'])
  await s.create('start_'+worker,{host,scope,name:string},T,{kind:'taskStart',scope:p('scope'),worker,context:ctx({host:p('host')}),item:p('name')})
 const singleton={kind:'listConstruct',itemType:T,items:[p('child')]}
 const first={kind:'listItemAt',list:pat(),index:{kind:'int64Literal',value:'0'}}
 const wait={kind:'taskWait',tasks:singleton,mode:'all'}
 await create('waitOne',{child:T,item:unit},bind(wait,{kind:'matchOption',input:first,
  someBody:{kind:'matchOption',input:pat(),someBody:pat(),noneBody:err(text('pending'))},noneBody:err(text('missing'))}))
 // Only the join is shielded; it does not mask the already-captured child's token.
 await create('joinOne',{child:T,item:unit},{kind:'cancellationShield',body:'waitOne',context:ctx({child:p('child')})})
 await create('waitOnlyCatch',{child:T,item:unit},catchWith('joinOne',{child:p('child')},'cancelHandler'))
 await s.create('cancelChild',{child:T},unit,{kind:'taskCancel',task:p('child')})
 for(const [policy,worker,join] of [['baseline','rawWorker','joinOne'],['waitOnly','rawWorker','waitOnlyCatch'],['adapted','adaptedWorker','joinOne'],['inverted','invertedWorker','joinOne'],['cleanup','cleanWorker','joinOne']]){
  await create(policy+'Body',{host,scope,cancel:bool},bind(direct('start_'+worker,{host:p('host'),scope:p('scope'),name:text('primary')}),
   bind(cond(p('cancel'),direct('cancelChild',{child:pat()}),unitValue),direct(join,{child:pat('value',1),item:unitValue}))))
  await create(policy+'Resource',{host,handle:unit,cancel:bool},{kind:'taskScope',body:policy+'Body',context:ctx({host:p('host'),cancel:p('cancel')})})
  await create(policy,{host,cancel:bool},{kind:'useResource',acquire:io('open'),releaseCapability:p('host'),capability:'Probe',releaseOperation:'close',body:policy+'Resource',context:ctx({host:p('host'),cancel:p('cancel')})})
 }
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {declarations,artifact,nodes:s.nodes}
}
