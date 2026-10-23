import {string,int,unit,cap,scope,task,oneShot,named,result,p,text,integer,unitValue,pat,bind,field,construct,ok,direct,cond,matchOption,invoke} from './lib.mjs'

// Minimal gate: can moving OneShot cursors release their past while workers live?
// It deliberately isolates retention before implementing a bounded backlog.
export async function author(s) {
  const link=named('Link'),cell=oneShot(link),R=result(int)
  const fields=o=>Object.entries(o).map(([displayName,type])=>({displayName,type}))
  const context=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
  const fresh=()=>({kind:'oneShotCreate',scope:p('scope'),valueType:link})
  const add=(a,b)=>({kind:'int64WrappingAdd',left:a,right:b})
  const loop=(fn,args)=>({...direct(fn,args),keepUnlisted:true})
  const wait=cursor=>({kind:'oneShotWait',cell:cursor,limit:{kind:'optionSome',value:integer(0)}})
  await s.call('open',{mode:'create',snapshot:s.snapshot})
  await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[
    {displayName:'Link',typeKind:{kind:'product',fields:fields({next:cell,sequence:int})}},
  ]}}))
  await s.publish(await s.call('request',{op:'simulateCreateEffect',arguments:{displayName:'Io'}}))
  await s.publish(await s.call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'Host',operations:[
    {displayName:'checkpoint',parameters:fields({worker:int,count:int}),resultType:unit,effect:'Io'},
  ]}}))
  await s.create('newCursor',{scope},cell,fresh(),[])
  await s.create('cursorWorker',{host:cap('Host'),scope,cursor:cell,count:int,index:int,worker:int},R,
    cond({kind:'boolNot',value:{kind:'int64LessThan',left:p('index'),right:p('count')}},
      bind(invoke(p('host'),'Host','checkpoint',{worker:p('worker'),count:p('index')}),ok(p('index'))),
      bind(fresh(),bind({kind:'oneShotComplete',cell:p('cursor'),value:construct('Link',{next:pat(),sequence:p('index')})},
        loop('cursorWorker',{cursor:pat('value',1),index:add(p('index'),integer(1))})))))
  await s.create('spawn',{host:cap('Host'),scope,cursor:cell,count:int,worker:int},task(R),
    {kind:'taskStart',scope:p('scope'),worker:'cursorWorker',item:p('worker'),context:context({host:p('host'),scope:p('scope'),cursor:p('cursor'),count:p('count'),index:integer(0)})})
  await s.create('retainedLength',{cursor:cell,count:int},int,
    matchOption(wait(p('cursor')),loop('retainedLength',{cursor:field(pat(),'Link','next'),count:add(p('count'),integer(1))}),p('count')),[])
  await s.create('scopeBody',{host:cap('Host'),scope,count:int},R,
    bind(fresh(),bind(direct('spawn',{host:p('host'),scope:p('scope'),cursor:pat(),count:p('count'),worker:integer(1)}),
      bind(fresh(),bind(direct('spawn',{host:p('host'),scope:p('scope'),cursor:pat(),count:p('count'),worker:integer(2)}),
        bind({kind:'taskWait',mode:'all',tasks:{kind:'listConstruct',itemType:task(R),items:[pat('value',2),pat()]}},ok(p('count'))))))))
  await s.create('run',{host:cap('Host'),count:int},R,
    {kind:'taskScope',body:'scopeBody',context:context({host:p('host'),count:p('count')})})
  const declarations=await s.call('inspect',{view:'declarations'})
  const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
  return {artifact,declarations,nodes:s.nodes}
}
