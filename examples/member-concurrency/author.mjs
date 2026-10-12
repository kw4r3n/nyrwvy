// Bounded admission is ordinary TaskScope/OneShot immutable-state composition.
// Effects hosts do not own registry, admission, cancellation or integration policy.
import {author as serialized} from '../member-controller/author.mjs'
import {string,int,bool,unit,unitValue,cap,scope,task,oneShot,list,option,result,named,p,pat,bind,field,construct,ok,err,direct,cond,matchOption,text,integer,boolean,concat,eqStr,eqInt,invoke,cancellation} from '../dynamic-worker-collector/lib.mjs'
const S=named('State'),Q=named('Request'),K=named('AdmissionTicket'),W=named('AdmissionOwner'),R=result(S),cell=oneShot(S),handle=task(R)
const fields=o=>Object.entries(o).map(([displayName,type])=>({displayName,type}))
const ctx=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
const f=(v,t,k)=>field(v,t,k),t=k=>f(p('ticket'),'AdmissionTicket',k),w=k=>f(p('owner'),'AdmissionOwner',k),q=k=>f(p('request'),'Request',k)
const update=(source,type,fields)=>({kind:'productUpdate',source,type,fields:Object.entries(fields).map(([field,value])=>({field,value}))})
const io=(op,args={})=>invoke(p('host'),'Host',op,args)
const gate=label=>io('gate',{label})
const reply=(label,record)=>io('reply',{label,record})
const L=(type,...items)=>({kind:'listConstruct',itemType:type,items})
const first=list=>({kind:'listItemAt',list,index:integer(0)})
const wait=target=>matchOption(first({kind:'taskWait',tasks:L(handle,target),mode:'all'}),matchOption(pat(),pat(),err(text('missing'),S)),err(text('missing'),S))
const observe=(cell,body,none)=>matchOption({kind:'oneShotWait',cell},body,none)
const poll=(cell,body,none)=>matchOption({kind:'oneShotWait',cell,limit:{kind:'optionSome',value:integer(0)}},body,none)
const complete=(cell,value)=>({kind:'oneShotComplete',cell,value})
const start=(worker,values)=>({kind:'taskStart',scope:p('scope'),worker,item:unitValue,context:ctx(values)})
const mr=(input,okBody,errBody)=>({kind:'matchResult',input,okBody,errBody})
const exclusive=op=>({kind:'boolOr',left:eqStr(op,text('spawn')),right:{kind:'boolOr',left:eqStr(op,text('integrate')),right:{kind:'boolOr',left:eqStr(op,text('discard')),right:eqStr(op,text('finish'))}}})

export async function author(s,{recoverCompleted=true}={}){
 await serialized(s)
 await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[
  {displayName:'AdmissionTicket',typeKind:{kind:'product',fields:fields({slot:int,parent:handle,relay:task(result(unit)),snapshot:cell,next:cell,exclusive:bool})}},
  {displayName:'AdmissionOwner',typeKind:{kind:'product',fields:fields({initial:S,last:cell,tickets:list(K)})}},
 ]}}))
 // Ownership release is an independent supervisor. A cancelled queued worker
 // returns promptly; its supervisor later forwards the unchanged predecessor.
 const relayParams={host:cap('Host'),previous:cell,next:cell,snapshot:cell,parent:handle,label:string,exclusive:bool,item:unit}
 await s.create('admissionPublish',{host:cap('Host'),next:cell,snapshot:cell,state:S,label:string},result(unit),
  bind(complete(p('snapshot'),p('state')),bind(gate(concat(text('released:'),p('label'))),bind(complete(p('next'),p('state')),ok(unitValue)))))
 await s.create('admissionRelay',relayParams,result(unit),
  bind(wait(p('parent')),cond(p('exclusive'),
   mr(pat(),direct('admissionPublish',{host:p('host'),next:p('next'),snapshot:p('snapshot'),state:pat(),label:p('label')}),
    observe(p('previous'),direct('admissionPublish',{host:p('host'),next:p('next'),snapshot:p('snapshot'),state:pat(),label:p('label')}),err(text('owner closed'),unit))),ok(unitValue))))
 const params={host:cap('Host'),scope,state:S,request:Q,next:cell}
 // Integration runs in this worker's cancellation context. The inherited
 // shield/catch composition retains HEAD before the supervisor releases state.
 await s.create('admissionIntegration',params,R,
  matchOption(direct('get',{state:p('state'),target:q('target')}),
   bind(direct('integrationWorker',{host:p('host'),entry:pat(),item:unitValue}),
    mr(pat(),ok(direct('acceptIntegration',{host:p('host'),state:p('state'),target:q('target'),label:q('label'),outcome:pat()})),err(pat('error'),S))),
   bind(reply(q('label'),text('err:unknown')),ok(p('state')))))
 await s.create('admissionCloseDrain',params,R,
  bind(gate(text('close-held-worker')),
   bind({kind:'listEffectFold',source:f(p('state'),'State','entries'),seed:unitValue,function:'stopFold',context:ctx({host:p('host')})},
    bind(reply(q('label'),text('ok:drained')),ok(p('state'))))))
 await s.create('admissionClose',params,R,
  bind(complete(f(p('state'),'State','closingSignal'),unitValue),
   bind(update(p('state'),'State',{closing:boolean(true)}),
    bind(complete(p('next'),pat()),direct('admissionCloseDrain',{...Object.fromEntries(Object.keys(params).map(k=>[k,p(k)])),state:pat('value',1)})))))
 await s.create('admissionDispatch',params,R,
  cond(eqStr(q('op'),text('integrate')),direct('admissionIntegration',Object.fromEntries(Object.keys(params).map(k=>[k,p(k)]))),
   cond(eqStr(q('op'),text('finish')),direct('admissionClose',Object.fromEntries(Object.keys(params).map(k=>[k,p(k)]))),
    ok(direct('dispatch',{host:p('host'),scope:p('scope'),state:p('state'),request:p('request')})))))
 const workerParams={host:cap('Host'),scope,previous:cell,next:cell,snapshot:cell,request:Q,item:unit}
 await s.create('admissionWorker',workerParams,R,
  bind(gate(concat(text('request-start:'),q('label'))),
   observe(p('previous'),
    cond(cancellation,bind(reply(q('label'),text('err:cancelled')),err(text('cancelled'),S)),
     bind(complete(p('snapshot'),pat()),bind(gate(concat(text('enter:'),q('label'))),
      direct('admissionDispatch',{host:p('host'),scope:p('scope'),state:pat('value',2),request:p('request'),next:p('next')})))),
    bind(reply(q('label'),text('err:cancelled')),err(text('cancelled'),S)))))
 // Readers use the latest published immutable snapshot, including the active
 // mutation's acquired state. They never await that mutation's whole request.
 const published=bind(poll(f(p('item'),'AdmissionTicket','snapshot'),pat(),p('acc')),
  poll(f(p('item'),'AdmissionTicket','next'),pat(),pat('value',1)))
 // Parent cancellation closes handoffs, but completed owned task results stay
 // observable. Recover only exclusive successful results: readers may carry
 // older state, and pending/Err/cancelled tasks must preserve the folded state.
 await s.create('admissionReadSnapshot',{acc:S,item:K},S,recoverCompleted?
  bind(published,cond(f(p('item'),'AdmissionTicket','exclusive'),
   matchOption(first({kind:'taskWait',tasks:L(handle,f(p('item'),'AdmissionTicket','parent')),mode:'all',limit:{kind:'optionSome',value:integer(0)}}),
    matchOption(pat(),mr(pat(),pat(),pat('value',3)),pat('value',2)),pat('value',1)),pat())):published,[])
 await s.create('admissionCurrent',{owner:W},S,
  {kind:'listEffectFold',source:w('tickets'),seed:w('initial'),function:'admissionReadSnapshot',context:[]},[])
 await s.create('admissionReader',{host:cap('Host'),scope,state:S,request:Q,item:unit},R,
  bind(gate(concat(text('request-start:'),q('label'))),ok(direct('dispatch',{host:p('host'),scope:p('scope'),state:p('state'),request:p('request')}))))
 const beginParams={host:cap('Host'),scope,owner:W,request:Q,next:cell,snapshot:cell,parent:handle}
 const actual=update(p('request'),'Request',{op:q('mode'),target:q('previous'),previous:integer(0),mode:text('all')})
 await s.create('admissionRegister',beginParams,W,
  bind(start('admissionRelay',{host:p('host'),previous:w('last'),next:p('next'),snapshot:p('snapshot'),parent:p('parent'),label:q('label'),exclusive:exclusive(q('mode'))}),
   update(p('owner'),'AdmissionOwner',{
    last:cond(exclusive(q('mode')),p('next'),w('last')),
    tickets:{kind:'listAppendItem',list:w('tickets'),item:construct('AdmissionTicket',{slot:q('target'),parent:p('parent'),relay:pat(),snapshot:p('snapshot'),next:p('next'),exclusive:exclusive(q('mode'))})}})))
 await s.create('admissionStart',{host:cap('Host'),scope,owner:W,request:Q,next:cell,snapshot:cell},W,
  bind(cond(exclusive(q('mode')),
   start('admissionWorker',{host:p('host'),scope:p('scope'),previous:w('last'),next:p('next'),snapshot:p('snapshot'),request:actual}),
   start('admissionReader',{host:p('host'),scope:p('scope'),state:direct('admissionCurrent',{owner:p('owner')}),request:actual})),
   direct('admissionRegister',{host:p('host'),scope:p('scope'),owner:p('owner'),request:p('request'),next:p('next'),snapshot:p('snapshot'),parent:pat()})))
 await s.create('admissionBegin',{host:cap('Host'),scope,owner:W,request:Q},W,
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:S},bind({kind:'oneShotCreate',scope:p('scope'),valueType:S},
   direct('admissionStart',{host:p('host'),scope:p('scope'),owner:p('owner'),request:p('request'),next:pat('value',1),snapshot:pat()}))))
 await s.create('admissionSlot',{target:int,item:K},bool,eqInt(f(p('item'),'AdmissionTicket','slot'),p('target')),[])
 // Find the latest ticket for a reused slot by folding, rather than maintaining
 // any mutable handle table outside the graph.
 await s.create('admissionLatest',{acc:option(K),item:K,target:int},option(K),
  cond(eqInt(f(p('item'),'AdmissionTicket','slot'),p('target')),{kind:'optionSome',value:p('item')},p('acc')),[])
 await s.create('admissionGet',{owner:W,target:int},option(K),
  {kind:'listFold',source:w('tickets'),seed:{kind:'optionNone',itemType:K},function:'admissionLatest',context:ctx({target:p('target')})},[])
 await s.create('admissionJoin',{host:cap('Host'),owner:W,ticket:K,cancel:bool},W,
  bind(cond(p('cancel'),{kind:'taskCancel',task:t('parent')},unitValue),
   bind(cond(p('cancel'),unitValue,bind(wait(t('parent')),unitValue)),p('owner'))))
 const joinTicket=cancel=>matchOption(direct('admissionGet',{owner:p('acc'),target:f(p('item'),'Request','target')}),direct('admissionJoin',{host:p('host'),owner:p('acc'),ticket:pat(),cancel:boolean(cancel)}),p('acc'))
 const regular=update(p('item'),'Request',{op:text('begin'),mode:f(p('item'),'Request','op'),previous:f(p('item'),'Request','target'),target:integer(0)})
 await s.create('admissionAwaitTicket',{owner:W,ticket:K},W,
  bind(wait(t('parent')),bind({kind:'taskWait',tasks:L(task(result(unit)),t('relay')),mode:'all'},p('owner'))),[])
 await s.create('admissionAwaitNew',{owner:W},W,
  matchOption(direct('admissionGet',{owner:p('owner'),target:integer(0)}),direct('admissionAwaitTicket',{owner:p('owner'),ticket:pat()}),p('owner')),[])
 await s.create('admissionSynchronous',{host:cap('Host'),scope,owner:W,request:Q},W,
  bind(direct('admissionBegin',{host:p('host'),scope:p('scope'),owner:p('owner'),request:p('request')}),
   direct('admissionAwaitNew',{owner:pat()})))
 await s.create('admissionFold',{host:cap('Host'),scope,acc:W,item:Q},W,
  cond(eqStr(f(p('item'),'Request','op'),text('begin')),direct('admissionBegin',{host:p('host'),scope:p('scope'),owner:p('acc'),request:p('item')}),
   cond(eqStr(f(p('item'),'Request','op'),text('join')),joinTicket(false),
    cond(eqStr(f(p('item'),'Request','op'),text('cancel')),joinTicket(true),
     cond(eqStr(f(p('item'),'Request','op'),text('gate')),bind(gate(f(p('item'),'Request','label')),p('acc')),
      direct('admissionSynchronous',{host:p('host'),scope:p('scope'),owner:p('acc'),request:regular}))))))
 await s.create('admissionDrainTicket',{acc:unit,item:K},unit,
  bind({kind:'taskWait',tasks:L(task(result(unit)),f(p('item'),'AdmissionTicket','relay')),mode:'all'},unitValue),[])
 await s.create('admissionLoop',{host:cap('Host'),scope,requests:list(Q),initial:S,last:cell},result(unit),
  bind(complete(p('last'),p('initial')),
   bind({kind:'listEffectFold',source:p('requests'),seed:construct('AdmissionOwner',{initial:p('initial'),last:p('last'),tickets:L(K)}),function:'admissionFold',context:ctx({host:p('host'),scope:p('scope')})},
    bind({kind:'listEffectFold',source:f(pat(),'AdmissionOwner','tickets'),seed:unitValue,function:'admissionDrainTicket',context:[]},ok(unitValue)))))
 await s.create('admissionScope',{host:cap('Host'),scope,requests:list(Q)},result(unit),
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:unit},
   bind(construct('State',{entries:L(named('Entry')),next:integer(1),closing:boolean(false),closingSignal:pat()}),
    bind({kind:'oneShotCreate',scope:p('scope'),valueType:S},direct('admissionLoop',{host:p('host'),scope:p('scope'),requests:p('requests'),initial:pat('value',1),last:pat()})))))
 await s.create('admissionResource',{host:cap('Host'),requests:list(Q),handle:unit},result(unit),
  {kind:'taskScope',body:'admissionScope',context:ctx({host:p('host'),requests:p('requests')})})
 await s.create('concurrentRun',{host:cap('Host'),requests:list(Q)},result(unit),
  {kind:'useResource',acquire:io('open'),releaseCapability:p('host'),capability:'Host',releaseOperation:'close',body:'admissionResource',context:ctx({host:p('host'),requests:p('requests')})})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,nodes:s.nodes}
}
