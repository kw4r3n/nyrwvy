import {string,int,bool,unit,cap,scope,task,oneShot,named,result,list,option,
  p,text,integer,boolean,unitValue,pat,bind,field,construct,ok,err,direct,cond,matchOption,invoke,cancellation,eqStr,eqInt} from './lib.mjs'

// Two independent one-record streams. Round-robin routing prevents a stalled
// input from leaving an old cursor into another producer's completed history.
export async function author(s) {
  const link=named('Link'),cell=oneShot(link),ack=oneShot(bool),gate=oneShot(unit),receipt=named('Receipt'),step=named('Step'),state=named('CollectorState'),R=result(string),RS=result(step),RR=result(receipt),RC=result(cell)
  const host=p('host'),fields=o=>Object.entries(o).map(([displayName,type])=>({displayName,type})),ctx=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
  const add=(a,b)=>({kind:'int64WrappingAdd',left:a,right:b}),ge=(a,b)=>({kind:'boolNot',value:{kind:'int64LessThan',left:a,right:b}}),and=(a,b)=>({kind:'boolAnd',left:a,right:b}),or=(a,b)=>({kind:'boolOr',left:a,right:b})
  const loop=(fn,args)=>({...direct(fn,args),keepUnlisted:true}),match=(input,okBody,errBody)=>({kind:'matchResult',input,okBody,errBody})
  const fresh=t=>({kind:'oneShotCreate',scope:p('scope'),valueType:t}),wait=x=>({kind:'oneShotWait',cell:x}),complete=(cell,value)=>({kind:'oneShotComplete',cell,value})
  const F=(x,f)=>field(x,'Link',f),P=(x,f)=>field(x,'Receipt',f),S=(x,f)=>field(x,'Step',f),io=(operation,args={})=>invoke(host,'Host',operation,args)
  const tasks=(...items)=>({kind:'listConstruct',itemType:task(R),items}),join=(items,mode='all',poll=false)=>({kind:'taskWait',tasks:items,mode,...(poll?{limit:{kind:'optionSome',value:integer(0)}}:{})})
  const callPublish=(cursor,id,kind,sequence,payload)=>direct('publish',{host,scope:p('scope'),cursor,id,kind,sequence,payload})
  const failed=(message,t=string)=>err(text(message),t)
  const startGuard=body=>matchOption(wait(p('gate')),body,failed('cancelled'))
  const stateFields={left:cell,right:cell,leftEnded:bool,rightEnded:bool,leftSequence:int,rightSequence:int,turnLeft:bool,delivered:int,stopping:bool}
  const C=(x,f)=>field(x,'CollectorState',f),stateWith=(source,changes)=>construct('CollectorState',Object.fromEntries(Object.keys(stateFields).map(k=>[k,changes[k]??C(source,k)])))
  await s.call('open',{mode:'create',snapshot:s.snapshot})
  await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[
    {displayName:'Link',typeKind:{kind:'product',fields:fields({id:string,kind:string,sequence:int,payload:string,next:cell,ack})}},
    {displayName:'Receipt',typeKind:{kind:'product',fields:fields({next:cell,ack,steps:int})}},
    {displayName:'CollectorState',typeKind:{kind:'product',fields:fields(stateFields)}},
    {displayName:'Step',typeKind:{kind:'product',fields:fields({next:cell,ended:bool,delivered:bool})}},
  ]}}))
  await s.publish(await s.call('request',{op:'simulateCreateEffect',arguments:{displayName:'Io'}}))
  const ops=[['open',{},result(unit)],['close',{handle:unit},unit],['read',{id:string,sequence:int},result(string)],['write',{id:string,sequence:int,payload:string},result(unit)],
    ['observe',{id:string,kind:string,sequence:int,accepted:bool,steps:int},unit],['waitAbort',{},unit],['finish',{outcome:string},unit]]
  await s.publish(await s.call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'Host',operations:ops.map(([displayName,parameters,resultType])=>({displayName,parameters:fields(parameters),resultType,effect:'Io'}))}}))
  await s.create('newCursor',{scope},cell,fresh(link),[])
  await s.create('newGate',{scope},gate,fresh(unit),[])
  // A successful completion is the accepted-send cutoff. Only Stop competes with
  // the sole producer of this stream; a losing sender must observe the winner.
  await s.create('appendTry',{cursor:cell,next:cell,ack,id:string,kind:string,sequence:int,payload:string,steps:int},RR,
    cond(cancellation,failed('cancelled',receipt),
      bind(complete(p('cursor'),construct('Link',{id:p('id'),kind:p('kind'),sequence:p('sequence'),payload:p('payload'),next:p('next'),ack:p('ack')})),
        cond(pat(),ok(construct('Receipt',{next:p('next'),ack:p('ack'),steps:add(p('steps'),integer(1))})),
          matchOption(wait(p('cursor')),
            cond(or(eqStr(F(pat(),'kind'),text('stop')),eqStr(F(pat(),'kind'),text('done'))),failed('stopped',receipt),
              loop('appendTry',{cursor:F(pat(),'next'),steps:add(p('steps'),integer(1))})),failed('closed',receipt))))),[])
  await s.create('append',{scope,cursor:cell,id:string,kind:string,sequence:int,payload:string},RR,
    bind(fresh(link),bind(fresh(bool),direct('appendTry',{cursor:p('cursor'),next:pat('value',1),ack:pat(),id:p('id'),kind:p('kind'),sequence:p('sequence'),payload:p('payload'),steps:integer(0)}))),[])
  await s.create('publish',{host:cap('Host'),scope,cursor:cell,id:string,kind:string,sequence:int,payload:string},RR,
    match(direct('append',{scope:p('scope'),cursor:p('cursor'),id:p('id'),kind:p('kind'),sequence:p('sequence'),payload:p('payload')}),
      bind(io('observe',{id:p('id'),kind:p('kind'),sequence:p('sequence'),accepted:boolean(true),steps:P(pat(),'steps')}),ok(pat('value',1))),
      bind(io('observe',{id:p('id'),kind:p('kind'),sequence:p('sequence'),accepted:boolean(false),steps:integer(0)}),err(pat('error',1),receipt))))
  await s.create('awaitAck',{packet:receipt},RC,
    matchOption(wait(P(p('packet'),'ack')),cond(and(pat(),{kind:'boolNot',value:cancellation}),ok(P(p('packet'),'next')),failed('cancelled',cell)),failed('cancelled',cell)),[])
  await s.create('producer',{host:cap('Host'),scope,gate,cursor:cell,id:string,count:int,sequence:int},R,
    startGuard(cond(cancellation,failed('cancelled'),
      cond(ge(p('sequence'),p('count')),
        match(callPublish(p('cursor'),p('id'),text('done'),p('sequence'),text('')),ok(text('done')),cond(eqStr(pat('error'),text('stopped')),ok(text('stopped')),err(pat('error')))),
        match(io('read',{id:p('id'),sequence:p('sequence')}),
          match(callPublish(p('cursor'),p('id'),text('data'),p('sequence'),pat()),
            match(direct('awaitAck',{packet:pat()}),loop('producer',{cursor:pat(),sequence:add(p('sequence'),integer(1))}),err(pat('error'))),
            cond(eqStr(pat('error'),text('stopped')),ok(text('stopped')),err(pat('error')))),err(pat('error')))))))
  const stepValue=(value,ended,delivered)=>construct('Step',{next:F(value,'next'),ended:boolean(ended),delivered:boolean(delivered)})
  // Link values remain only in this returning helper, never in a collector
  // branch-local variable that could preserve a dormant head indefinitely.
  await s.create('consume',{host:cap('Host'),cursor:cell,id:string,expected:int},RS,
    cond(cancellation,failed('cancelled',step),matchOption(wait(p('cursor')),
      cond(eqStr(F(pat(),'id'),p('id')),
        cond(eqStr(F(pat(),'kind'),text('data')),
          cond(eqInt(F(pat(),'sequence'),p('expected')),
            match(io('write',{id:p('id'),sequence:F(pat(),'sequence'),payload:F(pat(),'payload')}),
              bind(complete(F(pat('value',1),'ack'),boolean(true)),ok(stepValue(pat('value',2),false,true))),err(pat('error'),step)),failed('sequence mismatch',step)),
          cond(or(eqStr(F(pat(),'kind'),text('done')),eqStr(F(pat(),'kind'),text('stop'))),
            bind(complete(F(pat(),'ack'),boolean(true)),ok(stepValue(pat('value',1),true,false))),failed('unknown record kind',step))),failed('worker routing mismatch',step)),failed('closed',step))))
  await s.create('stopBoth',{host:cap('Host'),scope,state},unit,
    bind(cond(C(p('state'),'leftEnded'),unitValue,bind(callPublish(C(p('state'),'left'),text('w1'),text('stop'),integer(0),text('')),unitValue)),
      cond(C(p('state'),'rightEnded'),unitValue,bind(callPublish(C(p('state'),'right'),text('w2'),text('stop'),integer(0),text('')),unitValue))))
  await s.create('updateState',{state,step,left:bool},state,
    cond(p('left'),stateWith(p('state'),{left:S(p('step'),'next'),leftEnded:S(p('step'),'ended'),leftSequence:add(C(p('state'),'leftSequence'),cond(S(p('step'),'delivered'),integer(1),integer(0))),turnLeft:boolean(false),delivered:add(C(p('state'),'delivered'),cond(S(p('step'),'delivered'),integer(1),integer(0)))}),
      stateWith(p('state'),{right:S(p('step'),'next'),rightEnded:S(p('step'),'ended'),rightSequence:add(C(p('state'),'rightSequence'),cond(S(p('step'),'delivered'),integer(1),integer(0))),turnLeft:boolean(true),delivered:add(C(p('state'),'delivered'),cond(S(p('step'),'delivered'),integer(1),integer(0)))})),[])
  const consumeSide=left=>match(direct('consume',{host,cursor:C(p('state'),left?'left':'right'),id:text(left?'w1':'w2'),expected:C(p('state'),left?'leftSequence':'rightSequence')}),
    ok(direct('updateState',{state:p('state'),step:pat(),left:boolean(left)})),err(pat('error'),state))
  await s.create('collectorStep',{host:cap('Host'),scope,state,stopAfter:int},result(state),
    cond(and(and(ge(p('stopAfter'),integer(0)),ge(C(p('state'),'delivered'),p('stopAfter'))),{kind:'boolNot',value:C(p('state'),'stopping')}),
      bind(direct('stopBoth',{host,scope:p('scope'),state:p('state')}),ok(stateWith(p('state'),{stopping:boolean(true)}))),
      cond(C(p('state'),'leftEnded'),consumeSide(false),cond(C(p('state'),'rightEnded'),consumeSide(true),cond(C(p('state'),'turnLeft'),consumeSide(true),consumeSide(false))))))
  // One common self-tail-call site. Side-specific branch locals live only in the
  // returning helper, including after one source finishes much earlier.
  await s.create('collector',{host:cap('Host'),scope,gate,state,stopAfter:int,item:unit},R,
    startGuard(cond(cancellation,failed('cancelled'),cond(and(C(p('state'),'leftEnded'),C(p('state'),'rightEnded')),
      ok(cond(C(p('state'),'stopping'),text('stopped'),text('done'))),
      match(direct('collectorStep',{host,scope:p('scope'),state:p('state'),stopAfter:p('stopAfter')}),loop('collector',{state:pat()}),err(pat('error')))))))
  await s.create('snapshot',{task:task(R)},option(R),
    matchOption({kind:'listItemAt',list:join(tasks(p('task')),'all',true),index:integer(0)},pat(),{kind:'optionNone',itemType:R}),[])
  await s.create('producerAlive',{task:task(R)},result(bool),
    matchOption(direct('snapshot',{task:p('task')}),match(pat(),ok(boolean(false)),err(pat('error'),bool)),ok(boolean(true))),[])
  await s.create('monitor',{collector:task(R),left:task(R),right:task(R),leftAlive:bool,rightAlive:bool},R,
    bind(join({kind:'listConcat',left:tasks(p('collector')),right:{kind:'listConcat',left:cond(p('leftAlive'),tasks(p('left')),tasks()),right:cond(p('rightAlive'),tasks(p('right')),tasks())}},'any'),
      match(direct('producerAlive',{task:p('left')}),
        match(direct('producerAlive',{task:p('right')}),
          matchOption(direct('snapshot',{task:p('collector')}),pat(),loop('monitor',{leftAlive:pat('value',2),rightAlive:pat('value',1)})),err(pat('error'))),err(pat('error')))),[])
  const start=(fn,item,context)=>({kind:'taskStart',scope:p('scope'),worker:fn,item,context:ctx(context)})
  // Return only tasks. This frame must end before any coordinator waits, so it
  // cannot become a hidden owner of the initial stream heads.
  await s.create('launchWithHeads',{host:cap('Host'),scope,gate,left:cell,right:cell,leftCount:int,rightCount:int,stopAfter:int},list(task(R)),
    bind(start('producer',text('w1'),{host,scope:p('scope'),gate:p('gate'),cursor:p('left'),count:p('leftCount'),sequence:integer(0)}),
      bind(start('producer',text('w2'),{host,scope:p('scope'),gate:p('gate'),cursor:p('right'),count:p('rightCount'),sequence:integer(0)}),
        bind(start('collector',unitValue,{host,scope:p('scope'),gate:p('gate'),stopAfter:p('stopAfter'),state:construct('CollectorState',{left:p('left'),right:p('right'),leftEnded:boolean(false),rightEnded:boolean(false),leftSequence:integer(0),rightSequence:integer(0),turnLeft:boolean(true),delivered:integer(0),stopping:boolean(false)})}),
          tasks(pat(),pat('value',2),pat('value',1))))))
  await s.create('launch',{host:cap('Host'),scope,gate,leftCount:int,rightCount:int,stopAfter:int},list(task(R)),
    bind(fresh(link),bind(fresh(link),direct('launchWithHeads',{host,scope:p('scope'),gate:p('gate'),left:pat('value',1),right:pat(),leftCount:p('leftCount'),rightCount:p('rightCount'),stopAfter:p('stopAfter')}))))
  await s.create('awaitTasks',{tasks:list(task(R))},R,
    matchOption({kind:'listItemAt',list:p('tasks'),index:integer(0)},
      matchOption({kind:'listItemAt',list:p('tasks'),index:integer(1)},
        matchOption({kind:'listItemAt',list:p('tasks'),index:integer(2)},
          direct('monitor',{collector:pat('value',2),left:pat('value',1),right:pat(),leftAlive:boolean(true),rightAlive:boolean(true)}),failed('missing right task')),failed('missing left task')),failed('missing collector task')),[])
  await s.create('scopeBody',{host:cap('Host'),scope,leftCount:int,rightCount:int,stopAfter:int,mode:string},R,
    bind(fresh(unit),bind(direct('launch',{host,scope:p('scope'),gate:pat(),leftCount:p('leftCount'),rightCount:p('rightCount'),stopAfter:p('stopAfter')}),
      bind(complete(pat('value',1),unitValue),
        cond(eqStr(p('mode'),text('parent-exit')),bind(io('waitAbort'),failed('cancelled')),direct('awaitTasks',{tasks:pat('value',1)}))))))
  await s.create('resourceBody',{host:cap('Host'),leftCount:int,rightCount:int,stopAfter:int,mode:string,handle:unit},R,
    match({kind:'taskScope',body:'scopeBody',context:ctx({host,leftCount:p('leftCount'),rightCount:p('rightCount'),stopAfter:p('stopAfter'),mode:p('mode')})},
      cond(cancellation,failed('cancelled'),bind(io('finish',{outcome:pat()}),ok(pat('value',1)))),err(pat('error'))))
  await s.create('run',{host:cap('Host'),leftCount:int,rightCount:int,stopAfter:int,mode:string},R,
    {kind:'useResource',acquire:io('open'),releaseCapability:host,capability:'Host',releaseOperation:'close',body:'resourceBody',context:ctx({host,leftCount:p('leftCount'),rightCount:p('rightCount'),stopAfter:p('stopAfter'),mode:p('mode')})})
  const declarations=await s.call('inspect',{view:'declarations'})
  const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
  return {artifact,declarations,nodes:s.nodes}
}
