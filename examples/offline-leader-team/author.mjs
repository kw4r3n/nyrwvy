// Application composition through ordinary reviewed authoring. No core changes.
import {author as admission} from '../member-concurrency/author.mjs'
import {string,int,bool,unit,unitValue,cap,scope,task,oneShot,list,result,named,gen,p,pat,bind,field,construct,ok,err,direct,cond,matchOption,text,integer,boolean,concat,eqStr,invoke} from '../dynamic-worker-collector/lib.mjs'
const S=named('State'),Q=named('Request'),W=named('AdmissionOwner'),K=named('AdmissionTicket'),R=result(S),T=task(R),C=oneShot(S),Turn=named('LeaderTurn')
const ctx=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
const values=ps=>Object.fromEntries(Object.keys(ps).map(k=>[k,p(k)]))
const f=(v,t,k)=>field(v,t,k),q=k=>f(p('request'),'Request',k),w=k=>f(p('owner'),'AdmissionOwner',k)
const L=(t,...items)=>({kind:'listConstruct',itemType:t,items})
const io=(op,args)=>invoke(p('host'),'Host',op,args),gate=label=>io('gate',{label}),reply=(label,record)=>io('reply',{label,record})
const update=(source,type,fields)=>({kind:'productUpdate',source,type,fields:Object.entries(fields).map(([field,value])=>({field,value}))})
const mr=(input,okBody,errBody)=>({kind:'matchResult',input,okBody,errBody})
const fold=(source,seed,fn,context={})=>({kind:'listEffectFold',source,seed,function:fn,context:ctx(context)})
const exclusive=op=>['spawn','integrate','discard','finish'].map(x=>eqStr(op,text(x))).reduce((left,right)=>({kind:'boolOr',left,right}))
const start=(worker,context)=>({kind:'taskStart',scope:p('scope'),worker,item:unitValue,context:ctx(context)})
const wait=target=>matchOption({kind:'listItemAt',list:{kind:'taskWait',tasks:L(T,target),mode:'all'},index:integer(0)},matchOption(pat(),pat(),err(text('pending'),S)),err(text('missing'),S))
const caught=(body,bodyContext,handler,handlerContext)=>({kind:'exceptionCatch',body,bodyContext:ctx(bodyContext),handler,handlerContext:ctx(handlerContext)})
const workerParams={host:cap('Host'),scope,previous:C,next:C,snapshot:C,request:Q,item:unit}
const workerContext=values({...workerParams,item:undefined});delete workerContext.item
const exception=gen('exception')

export async function author(s,{wireTurn,sessionInput}={}){
 // Optional application input loop; the default finite turns and all lifetime,
 // admission and terminal functions keep the same bodies and ownership shape.
 const inputParams=sessionInput?.parameters??{turns:list(Turn)}, inputContext=values(inputParams)
 const foldInput=sessionInput?.fold??(owner=>fold(p('turns'),owner,'sessionTurn',{host:p('host'),scope:p('scope'),wrongBoundary:p('wrongBoundary')}))
 await admission(s) // Maintained defaults: linked provider cancellation, protected determined tail.
 await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[
  {displayName:'LeaderTurn',typeKind:{kind:'product',fields:[{displayName:'label',type:string},{displayName:wireTurn?'lines':'commands',type:list(wireTurn?string:Q)}]}}
 ]}}))
 // Only independently owned command workers choose a cancellation Result.
 // Neither Member workers, supervisor faults, cleanup nor release is adapted.
 await s.create('sessionCommandCancelled',{host:cap('Host'),request:Q},R,
  bind(reply(concat(q('label'),text(':outcome')),text('err:command-cancelled')),err(text('command-cancelled'),S)))
 await s.create('sessionCommandFault',{host:cap('Host'),request:Q,exception},R,
  cond({kind:'exceptionIsCancellation',value:p('exception')},direct('sessionCommandCancelled',{host:p('host'),request:p('request')}),
   {kind:'exceptionRethrow',value:p('exception'),resultType:R}))
 await s.create('sessionCommandCore',workerParams,R,direct('admissionWorker',values(workerParams)))
 const core=caught('sessionCommandCore',workerContext,'sessionCommandFault',{host:p('host'),request:p('request')})
 await s.create('sessionCommandAdapted',workerParams,R,
  bind(core,mr(pat(),ok(pat()),cond(eqStr(pat('error'),text('cancelled')),direct('sessionCommandCancelled',{host:p('host'),request:p('request')}),err(pat('error'),S)))))
 for(const [name,body] of [['sessionCommandWorker',direct('sessionCommandAdapted',values(workerParams))],['sessionCommandRaw',direct('sessionCommandCore',values(workerParams))]])
  await s.create(name,workerParams,R,bind(body,bind(gate(concat(text('command-cleanup:'),q('label'))),pat('value',1))))
 const begin={host:cap('Host'),scope,owner:W,request:Q,next:C,snapshot:C,wrongBoundary:bool}
 const actual=update(p('request'),'Request',{op:q('mode'),target:q('previous'),previous:integer(0),mode:text('all')})
 const args={host:p('host'),scope:p('scope'),previous:w('last'),next:p('next'),snapshot:p('snapshot'),request:actual}
 await s.create('sessionStart',begin,W,
  bind(cond(exclusive(q('mode')),cond(p('wrongBoundary'),start('sessionCommandRaw',args),start('sessionCommandWorker',args)),
   start('admissionReader',{host:p('host'),scope:p('scope'),state:direct('admissionCurrent',{owner:p('owner')}),request:actual})),
   direct('admissionRegister',{host:p('host'),scope:p('scope'),owner:p('owner'),request:p('request'),next:p('next'),snapshot:p('snapshot'),parent:pat()})))
 const beginParams={host:cap('Host'),scope,owner:W,request:Q,wrongBoundary:bool}
 await s.create('sessionBegin',beginParams,W,
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:S},bind({kind:'oneShotCreate',scope:p('scope'),valueType:S},
   direct('sessionStart',{...values(beginParams),next:pat('value',1),snapshot:pat()}))))
 await s.create('sessionWaitTicket',{ticket:K,item:unit},R,wait(f(p('ticket'),'AdmissionTicket','parent')),[])
 await s.create('sessionJoinFault',{host:cap('Host'),exception},R,
  cond({kind:'exceptionIsCancellation',value:p('exception')},bind(reply(text('@join-only'),text('err:command-cancelled')),err(text('command-cancelled'),S)),
   {kind:'exceptionRethrow',value:p('exception'),resultType:R}))
 await s.create('sessionJoin',{host:cap('Host'),owner:W,ticket:K,cancel:bool,wrongBoundary:bool},W,
  bind(cond(p('cancel'),{kind:'taskCancel',task:f(p('ticket'),'AdmissionTicket','parent')},
   bind(cond(p('wrongBoundary'),caught('sessionWaitTicket',{ticket:p('ticket')},'sessionJoinFault',{host:p('host')}),
    direct('sessionWaitTicket',{ticket:p('ticket'),item:unitValue})),unitValue)),p('owner')))
 const join=cancel=>matchOption(direct('admissionGet',{owner:p('acc'),target:f(p('item'),'Request','target')}),
  direct('sessionJoin',{host:p('host'),owner:p('acc'),ticket:pat(),cancel:boolean(cancel),wrongBoundary:p('wrongBoundary')}),p('acc'))
 const regular=update(p('item'),'Request',{op:text('begin'),mode:f(p('item'),'Request','op'),previous:f(p('item'),'Request','target'),target:integer(0)})
 const beginCall=request=>direct('sessionBegin',{host:p('host'),scope:p('scope'),owner:p('acc'),request,wrongBoundary:p('wrongBoundary')})
 await s.create('sessionCommandFold',{host:cap('Host'),scope,acc:W,item:Q,wrongBoundary:bool},W,
  cond(eqStr(f(p('item'),'Request','op'),text('begin')),beginCall(p('item')),
   cond(eqStr(f(p('item'),'Request','op'),text('join')),join(false),
    cond(eqStr(f(p('item'),'Request','op'),text('cancel')),join(true),
     cond(eqStr(f(p('item'),'Request','op'),text('gate')),bind(gate(f(p('item'),'Request','label')),p('acc')),
      bind(beginCall(regular),direct('admissionAwaitNew',{owner:pat()})))))))
 // The scripted Leader is graph data. Each turn receives a graph-built registry
 // report; requests cross the same admission path while tickets span turns.
 await s.create('sessionReport',{owner:W},string,
  fold(f(direct('admissionCurrent',{owner:p('owner')}),'State','entries'),text(''),'renderFold',{mode:text('inspect')}),[])
 if(wireTurn)await wireTurn(s)
 await s.create('sessionTurn',{host:cap('Host'),scope,acc:W,item:Turn,wrongBoundary:bool},W,
  bind(reply(concat(text('@leader:'),f(p('item'),'LeaderTurn','label')),direct('sessionReport',{owner:p('acc')})),
   fold(f(p('item'),'LeaderTurn',wireTurn?'lines':'commands'),p('acc'),wireTurn?'wireFold':'sessionCommandFold',{host:p('host'),scope:p('scope'),wrongBoundary:p('wrongBoundary')})))
 // Close cutoff precedes Member drainage. Then join every command AND supervisor
 // before the final registry inspection; scope exit is an additional safety net.
 await s.create('sessionDrainTicket',{acc:unit,item:K},unit,
  bind(wait(f(p('item'),'AdmissionTicket','parent')),
   bind({kind:'taskWait',tasks:L(task(result(unit)),f(p('item'),'AdmissionTicket','relay')),mode:'all'},unitValue)),[])
 await s.create('sessionTerminal',{host:cap('Host'),owner:W},result(string),
  bind(fold(f(p('owner'),'AdmissionOwner','tickets'),unitValue,'sessionDrainTicket'),
   bind(reply(text('@requests-joined'),text('joined')),
    bind(direct('sessionReport',{owner:p('owner')}),bind(reply(text('@terminal-inspected'),pat()),ok(pat('value',1)))))))
 await s.create('sessionFinish',{host:cap('Host'),scope,owner:W,wrongBoundary:bool},result(string),
  bind(direct('sessionBegin',{host:p('host'),scope:p('scope'),owner:p('owner'),wrongBoundary:p('wrongBoundary'),
   request:construct('Request',{op:text('begin'),target:integer(0),previous:integer(0),mode:text('finish'),limit:integer(5000),label:text('@auto-close')})}),
   bind(direct('admissionAwaitNew',{owner:pat()}),direct('sessionTerminal',{host:p('host'),owner:pat()}))))
 await s.create('sessionScope',{host:cap('Host'),scope,...inputParams,closingSignal:oneShot(unit),wrongBoundary:bool},result(string),
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:S},
   bind(construct('State',{entries:L(named('Entry')),next:integer(1),closing:boolean(false),closingSignal:p('closingSignal')}),
    bind({kind:'oneShotComplete',cell:pat('value',1),value:pat()},
     bind(foldInput(construct('AdmissionOwner',{initial:pat('value',1),last:pat('value',2),tickets:L(K)})),
      direct('sessionFinish',{host:p('host'),scope:p('scope'),owner:pat(),wrongBoundary:p('wrongBoundary')}))))))
 await s.create('sessionCancellable',{host:cap('Host'),...inputParams,closingSignal:oneShot(unit),wrongBoundary:bool,item:unit},result(string),
  {kind:'taskScope',body:'sessionScope',context:ctx({host:p('host'),...inputContext,closingSignal:p('closingSignal'),wrongBoundary:p('wrongBoundary')})})
 // Reuse the protected close-latch lifetime shape; other cells and all work
 // remain linked to the cancellable execution owner.
 await s.create('sessionCloseLifetime',{host:cap('Host'),...inputParams,executionScope:scope,scope,wrongBoundary:bool},result(string),
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:unit},
   bind({kind:'taskStart',scope:p('executionScope'),worker:'sessionCancellable',item:unitValue,context:ctx({host:p('host'),...inputContext,closingSignal:pat(),wrongBoundary:p('wrongBoundary')})},
    matchOption({kind:'listItemAt',list:{kind:'taskWait',tasks:L(task(result(string)),pat()),mode:'all'},index:integer(0)},matchOption(pat(),pat(),err(text('pending'))),err(text('missing'))))))
 await s.create('sessionCloseShield',{host:cap('Host'),...inputParams,executionScope:scope,wrongBoundary:bool,item:unit},result(string),
  {kind:'taskScope',body:'sessionCloseLifetime',context:ctx({host:p('host'),...inputContext,executionScope:p('executionScope'),wrongBoundary:p('wrongBoundary')})})
 await s.create('sessionExecutionScope',{host:cap('Host'),...inputParams,scope,wrongBoundary:bool},result(string),
  {kind:'cancellationShield',body:'sessionCloseShield',context:ctx({host:p('host'),...inputContext,executionScope:p('scope'),wrongBoundary:p('wrongBoundary')})})
 await s.create('sessionResource',{host:cap('Host'),...inputParams,handle:unit,wrongBoundary:bool},result(string),
  {kind:'taskScope',body:'sessionExecutionScope',context:ctx({host:p('host'),...inputContext,wrongBoundary:p('wrongBoundary')})})
 await s.create('sessionRun',{host:cap('Host'),...inputParams,wrongBoundary:bool},result(string),
  mr({kind:'useResource',acquire:io('open',{}),releaseCapability:p('host'),capability:'Host',releaseOperation:'close',body:'sessionResource',context:ctx({host:p('host'),...inputContext,wrongBoundary:p('wrongBoundary')})},
   bind(reply(text('terminal'),pat()),ok(pat('value',1))),err(pat('error'))))
 // Compiled placement control uses the identical command worker and acceptance
 // contract. Catching its wait chooses a local Err but leaves the owned task
 // faulted, so TaskScope drainage still rethrows the original cancellation.
 await s.create('sessionProbeStart',{host:cap('Host'),scope,previous:C,next:C,snapshot:C,wrongBoundary:bool},T,
  cond(p('wrongBoundary'),start('sessionCommandRaw',{host:p('host'),scope:p('scope'),previous:p('previous'),next:p('next'),snapshot:p('snapshot'),request:construct('Request',{op:text('spawn'),target:integer(0),previous:integer(0),mode:text('all'),limit:integer(5000),label:text('cancelled')})}),
   start('sessionCommandWorker',{host:p('host'),scope:p('scope'),previous:p('previous'),next:p('next'),snapshot:p('snapshot'),request:construct('Request',{op:text('spawn'),target:integer(0),previous:integer(0),mode:text('all'),limit:integer(5000),label:text('cancelled')})})))
 await s.create('sessionProbeWait',{child:T,item:unit},R,wait(p('child')),[])
 await s.create('sessionProbeJoin',{host:cap('Host'),child:T,wrongBoundary:bool},R,
  bind(gate(text('queued:cancelled')),bind({kind:'taskCancel',task:p('child')},
   cond(p('wrongBoundary'),caught('sessionProbeWait',{child:p('child')},'sessionJoinFault',{host:p('host')}),direct('sessionProbeWait',{child:p('child'),item:unitValue})))))
 await s.create('sessionProbeScope',{host:cap('Host'),scope,wrongBoundary:bool},R,
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:S},bind({kind:'oneShotCreate',scope:p('scope'),valueType:S},bind({kind:'oneShotCreate',scope:p('scope'),valueType:S},
   bind(direct('sessionProbeStart',{host:p('host'),scope:p('scope'),wrongBoundary:p('wrongBoundary'),previous:pat('value',2),next:pat('value',1),snapshot:pat()}),
    direct('sessionProbeJoin',{host:p('host'),child:pat(),wrongBoundary:p('wrongBoundary')}))))))
 await s.create('sessionProbeResource',{host:cap('Host'),handle:unit,wrongBoundary:bool},R,
  {kind:'taskScope',body:'sessionProbeScope',context:ctx({host:p('host'),wrongBoundary:p('wrongBoundary')})})
 await s.create('sessionBoundaryProbe',{host:cap('Host'),wrongBoundary:bool},R,
  {kind:'useResource',acquire:io('open',{}),releaseCapability:p('host'),capability:'Host',releaseOperation:'close',body:'sessionProbeResource',context:ctx({host:p('host'),wrongBoundary:p('wrongBoundary')})})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,declarations,nodes:s.nodes}
}
