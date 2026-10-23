import { string, int, bool, unit, cap, scope, task, oneShot, list, option, result, named,
  p, text, integer, boolean, unitValue, pat, bind, field, construct, ok, err, direct,
  cond, matchOption, concat, eqStr, eqInt, invoke, cancellation } from './lib.mjs'

export async function author(s) {
  const R = result(string), link = named('Link'), cell = oneShot(link), packet = named('Settled')
  const outcome = result(packet), workers = list(task(R)), state = {kind:'builtinGeneric',generic:'map',arguments:[string,int]}
  const fields = o => Object.entries(o).map(([displayName,type])=>({displayName,type}))
  const loop = (fn, values={})=>({...direct(fn,values),keepUnlisted:true})
  const host=p('host'), io=(operation,values={})=>invoke(host,'Host',operation,values)
  const wait=cell=>({kind:'oneShotWait',cell}), complete=(cell,value)=>({kind:'oneShotComplete',cell,value})
  const start=(worker,item,context)=>({kind:'taskStart',scope:p('scope'),worker,item,context:fields(context).map(({displayName,type})=>({parameter:displayName,value:type}))})
  const tasks=(...items)=>({kind:'listConstruct',itemType:task(R),items})
  const join=tasks=>({kind:'taskWait',tasks,mode:'all'})
  const cancel=task=>({kind:'taskCancel',task})
  const F=(source,f)=>field(source,'Link',f), P=(source,f)=>field(source,'Settled',f)
  const add=(a,b)=>({kind:'int64WrappingAdd',left:a,right:b})
  const and=(a,b)=>({kind:'boolAnd',left:a,right:b}), or=(a,b)=>({kind:'boolOr',left:a,right:b})
  const matchResult=(input,okBody,errBody)=>({kind:'matchResult',input,okBody,errBody})
  const params={scope,head:cell,id:string,kind:string,sequence:int}
  const callAppend=(id,kind,sequence)=>direct('append',{scope:p('scope'),head:p('head'),id,kind,sequence})
  const stop=()=>direct('publish',{host,scope:p('scope'),head:p('head'),id:text('control'),kind:text('stop'),sequence:integer(0)})
  const emptyState={kind:'mapEmpty',keyType:string,valueType:int}
  const collect=(tail,seen,log)=>direct('collect',{tail,seen,log})
  await s.call('open',{mode:'create',snapshot:s.snapshot})
  await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[
    {displayName:'Link',typeKind:{kind:'product',fields:fields({id:string,kind:string,sequence:int,next:cell})}},
    {displayName:'Settled',typeKind:{kind:'product',fields:fields({head:cell,live:option(R)})}},
  ]}}))
  await s.publish(await s.call('request',{op:'simulateCreateEffect',arguments:{displayName:'Io'}}))
  const operations=[['open',{},result(unit)],['close',{handle:unit},unit],['pause',{id:string,phase:string},result(unit)],
    ['observe',{id:string,kind:string,sequence:int,accepted:bool},unit],['ready',{},unit],['save',{report:string},unit]]
  await s.publish(await s.call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'Host',operations:operations.map(([displayName,parameters,resultType])=>({displayName,parameters:fields(parameters),resultType,effect:'Io'}))}}))

  // A successful Complete is the only accepted-send linearization point. Stop competes
  // for the same slot as data. Allocate next once per logical append, not once per retry.
  await s.create('appendTry',{tail:cell,next:cell,id:string,kind:string,sequence:int},bool,
    cond(cancellation,boolean(false),
      bind(complete(p('tail'),construct('Link',{id:p('id'),kind:p('kind'),sequence:p('sequence'),next:p('next')})),
        cond(pat(),boolean(true),matchOption(wait(p('tail')),
          cond(eqStr(F(pat(),'kind'),text('stop')),boolean(false),loop('appendTry',{tail:F(pat(),'next')})),boolean(false))))),[])
  await s.create('append',params,bool,
    bind({kind:'oneShotCreate',scope:p('scope'),valueType:link},direct('appendTry',{tail:p('head'),next:pat(),id:p('id'),kind:p('kind'),sequence:p('sequence')})),[])
  await s.create('publish',{host:cap('Host'),...params},bool,
    bind(callAppend(p('id'),p('kind'),p('sequence')),
      bind(io('observe',{id:p('id'),kind:p('kind'),sequence:p('sequence'),accepted:pat()}),pat('value',1))))

  // Routing and validation are graph-owned. -1 = unregistered; -2 = terminal.
  // No cancellation guard here: completed values win even after owner closure.
  const linkAt=()=>pat('value',1), kind=()=>F(linkAt(),'kind'), seq=()=>F(linkAt(),'sequence'), id=()=>F(linkAt(),'id')
  const registration=and(eqStr(kind(),text('register')),and(eqInt(seq(),integer(0)),eqInt(pat(),integer(-1))))
  const terminal=or(eqStr(kind(),text('final')),eqStr(kind(),text('error')))
  const progress=and(eqStr(kind(),text('progress')),or(eqInt(seq(),integer(1)),eqInt(seq(),integer(2))))
  const end=or(and(eqStr(kind(),text('final')),eqInt(seq(),integer(3))),and(eqStr(kind(),text('error')),or(eqInt(seq(),integer(1)),or(eqInt(seq(),integer(2)),eqInt(seq(),integer(3))))))
  const continuation=and(eqInt(seq(),pat()),or(progress,end))
  await s.create('collect',{tail:cell,seen:state,log:string},R,
    matchOption(wait(p('tail')),
      cond(eqStr(F(pat(),'kind'),text('stop')),ok(p('log')),
        bind({kind:'optionGetOrDefault',option:{kind:'mapGet',map:p('seen'),key:F(pat(),'id')},default:integer(-1)},
          cond(or(registration,continuation),
            collect(F(linkAt(),'next'),{kind:'mapInsert',map:p('seen'),key:id(),value:cond(terminal,integer(-2),add(seq(),integer(1)))},
              concat(p('log'),id(),text(':'),kind(),text(':'),{kind:'int64ToString',value:seq()},text(';'))),
            err(concat(text('invalid event after '),p('log')))))),ok(p('log'))),[])
  await s.create('collector',{host:cap('Host'),head:cell,ready:oneShot(unit),item:unit},R,
    bind(io('ready'),bind(complete(p('ready'),unitValue),collect(p('head'),emptyState,text('')))))

  const publish=(kind,sequence)=>direct('publish',{host,scope:p('scope'),head:p('head'),id:p('id'),kind:text(kind),sequence:integer(sequence)})
  const failed=()=>err(text('rejected'))
  const stage=(phase,sequence,body)=>
    matchResult(io('pause',{id:p('id'),phase:text(phase)}),
      cond(cancellation,err(text('cancelled')),bind(publish('progress',sequence),cond(pat(),body,failed()))),
      bind(publish('error',sequence),err(text('worker failed'))))
  await s.create('worker',{host:cap('Host'),scope,head:cell,id:string},R,
    matchResult(io('pause',{id:p('id'),phase:text('join')}),
      cond(cancellation,err(text('cancelled')),bind(publish('register',0),cond(pat(),
        stage('p1',1,stage('p2',2,
          matchResult(io('pause',{id:p('id'),phase:text('final')}),
            cond(cancellation,err(text('cancelled')),bind(publish('final',3),cond(pat(),ok(text('done')),failed()))),
            bind(publish('error',3),err(text('worker failed')))))),failed()))),err(text('join failed'))))
  await s.create('startWorkers',{host:cap('Host'),scope,head:cell,count:int,index:int,started:workers},workers,
    cond({kind:'int64LessThan',left:p('count'),right:p('index')},p('started'),
      bind(start('worker',concat(text('w'),{kind:'int64ToString',value:p('index')}),{host,scope:p('scope'),head:p('head')}),
        loop('startWorkers',{index:add(p('index'),integer(1)),started:{kind:'listConcat',left:p('started'),right:tasks(pat())}}))))
  await s.create('cancelWorker',{host:cap('Host'),target:task(R),item:unit},R,
    bind(io('pause',{id:text('control'),phase:text('cancel')}),bind(cancel(p('target')),ok(text('cancel sent')))))
  await s.create('stopCollector',{host:cap('Host'),scope,head:cell,item:unit},R,
    bind(io('pause',{id:text('control'),phase:text('stop')}),bind(stop(),ok(text('stop sent')))))
  await s.create('settled',{head:cell,states:list(option(R))},outcome,
    ok(construct('Settled',{head:p('head'),live:matchOption({kind:'listItemAt',list:p('states'),index:integer(0)},pat(),{kind:'optionNone',itemType:R})})),[])
  await s.create('finish',{host:cap('Host'),scope,head:cell,workers,collectorTask:task(R)},outcome,
    bind(join(p('workers')),bind(stop(),
      bind(join(tasks(p('collectorTask'))),direct('settled',{head:p('head'),states:pat()})))))
  const finish=()=>direct('finish',{host,scope:p('scope'),head:p('head'),workers:p('workers'),collectorTask:p('collectorTask')})
  const early=()=>bind(io('pause',{id:text('control'),phase:text('exit')}),ok(construct('Settled',{head:p('head'),live:{kind:'optionNone',itemType:R}})))
  await s.create('coordinate',{host:cap('Host'),scope,head:cell,workers,collectorTask:task(R),mode:string},outcome,
    cond(eqStr(p('mode'),text('parent-exit')),early(),
      cond(eqStr(p('mode'),text('cancel-one')),
        matchOption({kind:'listItemAt',list:p('workers'),index:integer(1)},
          bind(start('cancelWorker',unitValue,{host,target:pat()}),finish()),finish()),
        cond(eqStr(p('mode'),text('stop-race')),bind(start('stopCollector',unitValue,{host,scope:p('scope'),head:p('head')}),finish()),finish()))))
  await s.create('launch',{host:cap('Host'),scope,head:cell,ready:oneShot(unit),count:int,mode:string},outcome,
    bind(start('collector',unitValue,{host,head:p('head'),ready:p('ready')}),
      matchOption(wait(p('ready')),
        bind(direct('startWorkers',{host,scope:p('scope'),head:p('head'),count:p('count'),index:integer(1),started:tasks()}),
          direct('coordinate',{host,scope:p('scope'),head:p('head'),workers:pat(),collectorTask:pat('value',2),mode:p('mode')})),
        ok(construct('Settled',{head:p('head'),live:{kind:'optionNone',itemType:R}})))))
  await s.create('scopeBody',{host:cap('Host'),scope,count:int,mode:string},outcome,
    bind({kind:'oneShotCreate',scope:p('scope'),valueType:link},
      bind({kind:'oneShotCreate',scope:p('scope'),valueType:unit},direct('launch',{host,scope:p('scope'),head:pat('value',1),ready:pat(),count:p('count'),mode:p('mode')}))))
  await s.create('verifyLive',{live:option(R),report:string},R,
    matchOption(p('live'),matchResult(pat(),cond(eqStr(pat(),p('report')),ok(p('report')),err(text('live/reconciled mismatch'))),err(pat('error'))),err(text('collector unfinished'))),[])
  await s.create('reconcile',{host:cap('Host'),packet,mode:string},R,
    matchResult(collect(P(p('packet'),'head'),emptyState,text('')),
      cond(or(cancellation,eqStr(p('mode'),text('parent-exit'))),err(concat(text('cancelled|'),pat())),
        matchResult(direct('verifyLive',{live:P(p('packet'),'live'),report:pat()}),
          cond(cancellation,err(concat(text('cancelled|'),pat())),bind(io('save',{report:pat()}),ok(pat('value',1)))),err(pat('error')))),err(pat('error'))))
  await s.create('resourceBody',{host:cap('Host'),count:int,mode:string,handle:unit},R,
    matchResult({kind:'taskScope',body:'scopeBody',context:fields({host,count:p('count'),mode:p('mode')}).map(({displayName,type})=>({parameter:displayName,value:type}))},
      direct('reconcile',{host,packet:pat(),mode:p('mode')}),err(pat('error'))))
  await s.create('run',{host:cap('Host'),count:int,mode:string},R,
    cond(or({kind:'int64LessThan',left:p('count'),right:integer(2)},{kind:'int64LessThan',left:integer(4),right:p('count')}),err(text('worker count must be 2..4')),
      {kind:'useResource',acquire:io('open'),releaseCapability:host,capability:'Host',releaseOperation:'close',body:'resourceBody',context:fields({host,count:p('count'),mode:p('mode')}).map(({displayName,type})=>({parameter:displayName,value:type}))}))
  const declarations=await s.call('inspect',{view:'declarations'})
  const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
  return {artifact,declarations,nodes:s.nodes}
}
