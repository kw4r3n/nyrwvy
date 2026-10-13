// Ordinary authoring recipes only. No snapshot construction or generated-source edits.
import {string,int,bool,unit,cap,scope,task,oneShot,list,option,result,named,
 p,text,integer,boolean,unitValue,pat,bind,field,construct,ok,err,direct,cond,
 matchOption,concat,eqStr,eqInt,invoke,cancellation} from '../dynamic-worker-collector/lib.mjs'
const fields=o=>Object.entries(o).map(([displayName,type])=>({displayName,type}))
const context=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
const R=result(named('Final')), E=named('Entry'), S=named('State'), Q=named('Request')
const entries=list(E), requests=list(Q), cell=oneShot(string), handle=task(R)
const closingCell=oneShot(unit)
const F=(v,f)=>field(v,'Final',f), A=(v,f)=>field(v,'Entry',f), T=(v,f)=>field(v,'State',f), C=(v,f)=>field(v,'Request',f)
const io=(op,args={})=>invoke(p('host'),'Host',op,args)
const mr=(input,okBody,errBody)=>({kind:'matchResult',input,okBody,errBody})
const some=value=>({kind:'optionSome',value}), none=t=>({kind:'optionNone',itemType:t})
const L=(t,...items)=>({kind:'listConstruct',itemType:t,items})
const cat=(a,b)=>({kind:'listConcat',left:a,right:b})
const add=(a,b)=>({kind:'int64WrappingAdd',left:a,right:b})
const gt=(a,b)=>({kind:'int64GreaterThan',left:a,right:b})
const and=(a,b)=>({kind:'boolAnd',left:a,right:b})
const or=(a,b)=>({kind:'boolOr',left:a,right:b})
const not=x=>({kind:'boolNot',value:x})
const num=x=>({kind:'int64ToString',value:x})
const update=(source,type,values)=>({kind:'productUpdate',source,type,fields:Object.entries(values).map(([field,value])=>({field,value}))})
const wait=(tasks,mode='all',limit)=>({kind:'taskWait',tasks,mode,...(limit?{limit:some(limit)}:{})})
const poll=e=>wait(L(handle,A(e,'handle')),'all',integer(0))
const first=l=>({kind:'listItemAt',list:l,index:integer(0)})
const phase=e=>({kind:'oneShotWait',cell:A(e,'phase'),limit:some(integer(0))})
const start=(worker,item,ctx)=>({kind:'taskStart',scope:p('scope'),worker,item,context:context(ctx)})
const map=(source,fn,ctx={})=>({kind:'listMap',source,function:fn,context:context(ctx)})
const filter=(source,fn,ctx={})=>({kind:'listFilter',source,function:fn,context:context(ctx)})
const fold=(source,seed,fn,ctx={},effect=false)=>({kind:effect?'listEffectFold':'listFold',source,seed,function:fn,context:context(ctx)})
const final=(status,commit=text(''),checks=text(''))=>construct('Final',{status,commit,checks})
const state=(es,next,closing,closingSignal)=>construct('State',{entries:es,next,closing,closingSignal})
const stopped=()=>ok(final(text('stopped')))

// Optional hooks compose additional ordinary recipes; default fixtures stay unchanged.
export async function author(s, extension) {
 await s.call('open',{mode:'create',snapshot:s.snapshot})
 await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[
  {displayName:'Final',typeKind:{kind:'product',fields:fields({status:string,commit:string,checks:string})}},
  {displayName:'Entry',typeKind:{kind:'product',fields:fields({id:int,workspace:string,base:string,previous:int,discarded:bool,superseded:bool,phase:cell,commitSignal:cell,handle,...extension?.entryFields})}},
  {displayName:'State',typeKind:{kind:'product',fields:fields({entries,next:int,closing:bool,closingSignal:closingCell})}},
  {displayName:'Request',typeKind:{kind:'product',fields:fields({op:string,target:int,previous:int,mode:string,limit:int,label:string})}},
 ]}}))
 await s.publish(await s.call('request',{op:'simulateCreateEffect',arguments:{displayName:'Io'}}))
 const ops=[['open',{},result(unit)],['close',{handle:unit},unit],['workspaceCreate',{workspace:string,base:string},result(unit)],
 ['workspaceDiscard',{workspace:string},result(unit)],['provider',{id:int,workspace:string,previous:int},result(string)],
 ['memberClose',{id:int},unit],['snapshot',{id:int,workspace:string},result(string)],['checks',{id:int,commit:string},string],
 ['gate',{label:string},unit],['reply',{label:string,record:string},unit],...(extension?.operations??[])]
 await s.publish(await s.call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'Host',operations:ops.map(([displayName,params,resultType])=>({displayName,parameters:fields(params),resultType,effect:'Io'}))}}))
 // All live handles are confined to graph state and task scopes.
 await s.create('providerTask',{host:cap('Host'),workspace:string,previous:int,id:int},result(string),
  cond(cancellation,err(text('cancelled')),io('provider',{id:p('id'),workspace:p('workspace'),previous:p('previous')})))
 await s.create('providerJoin',{target:task(result(string)),limit:int},result(string),
  matchOption(first(wait(L(task(result(string)),p('target')),'all',p('limit'))),
   matchOption(pat(),mr(pat(),ok(pat()),err(text('provider-failed'))),bind({kind:'taskCancel',task:p('target')},bind(wait(L(task(result(string)),p('target'))),err(text('deadline'))))),err(text('invalid task'))),[])
 await s.create('providerScope',{host:cap('Host'),scope,workspace:string,previous:int,id:int,deadline:int},result(string),
  bind(start('providerTask',p('id'),{host:p('host'),workspace:p('workspace'),previous:p('previous')}),direct('providerJoin',{target:pat(),limit:p('deadline')})))
 await s.create('afterChecks',{host:cap('Host'),id:int,commit:string,closingSignal:closingCell},R,
  matchOption({kind:'oneShotWait',cell:p('closingSignal'),limit:some(integer(0))},ok(final(text('completed'),p('commit'))),
   bind(io('checks',{id:p('id'),commit:p('commit')}),ok(final(text('completed'),p('commit'),pat())))))
 // Opt-in snapshot recovery rethrows original cancellation and recovers ordinary
 // exceptions. Publication, checks and cleanup stay outside the boundary.
 if(extension?.containSnapshotFaults){
  await s.create('snapshotInvoke',{host:cap('Host'),id:int,workspace:string,item:unit},result(string),
   io('snapshot',{id:p('id'),workspace:p('workspace')}))
  await s.create('snapshotFault',{exception:{kind:'builtinGeneric',generic:'exception',arguments:[]}},result(string),
   cond({kind:'exceptionIsCancellation',value:p('exception')},
    {kind:'exceptionRethrow',value:p('exception'),resultType:result(string)},
    err({kind:'exceptionMessage',value:p('exception')})),[])
 }
 const snapshotResult=extension?.containSnapshotFaults?
  {kind:'exceptionCatch',body:'snapshotInvoke',bodyContext:context({host:p('host'),id:p('id'),workspace:p('workspace')}),handler:'snapshotFault',handlerContext:[]}:io('snapshot',{id:p('id'),workspace:p('workspace')})
 await s.create('snapshot',{host:cap('Host'),id:int,workspace:string,closingSignal:closingCell,commitSignal:cell},R,
  mr(snapshotResult,bind({kind:'oneShotComplete',cell:p('commitSignal'),value:pat()},direct('afterChecks',{host:p('host'),id:p('id'),commit:pat('value',1),closingSignal:p('closingSignal')})),ok(final(text('failed')))))
 await s.create('determined',{host:cap('Host'),id:int,workspace:string,phase:cell,status:string,closingSignal:closingCell,commitSignal:cell},R,
  bind({kind:'oneShotComplete',cell:p('phase'),value:p('status')},
   cond(eqStr(p('status'),text('completed')),direct('snapshot',{host:p('host'),id:p('id'),workspace:p('workspace'),closingSignal:p('closingSignal'),commitSignal:p('commitSignal')}),ok(final(p('status'))))))
 // Capture status in the worker's original context, before cleanup. Shield
 // only the determined tail; the existing provider scope stays cancellable.
 if(extension?.protectDetermined)await s.create('memberSettlement',{host:cap('Host'),id:int,workspace:string,phase:cell,status:string,closingSignal:closingCell,commitSignal:cell,item:unit},R,
  bind({kind:'oneShotComplete',cell:p('phase'),value:p('status')},
   bind(io('memberClose',{id:p('id')}),
    cond(eqStr(p('status'),text('completed')),direct('snapshot',{host:p('host'),id:p('id'),workspace:p('workspace'),closingSignal:p('closingSignal'),commitSignal:p('commitSignal')}),ok(final(p('status')))))))
 const status=cond(cancellation,text('stopped'),mr(pat(),pat(),cond(eqStr(pat('error'),text('deadline')),text('timed_out'),text('failed'))))
 const settlementContext={host:p('host'),id:p('id'),workspace:p('workspace'),phase:p('phase'),closingSignal:p('closingSignal'),commitSignal:p('commitSignal'),status:pat()}
 await s.create('worker',{host:cap('Host'),workspace:string,previous:int,phase:cell,deadline:int,id:int,closingSignal:closingCell,commitSignal:cell},R,
  bind(io('gate',{label:concat(text('worker-entry:'),num(p('id')))}),
   cond(cancellation,direct('determined',{host:p('host'),id:p('id'),workspace:p('workspace'),phase:p('phase'),closingSignal:p('closingSignal'),commitSignal:p('commitSignal'),status:text('stopped')}),
    bind({kind:'taskScope',body:'providerScope',context:context({host:p('host'),workspace:p('workspace'),previous:p('previous'),id:p('id'),deadline:p('deadline')})},
     extension?.protectDetermined?bind(status,{kind:'cancellationShield',body:'memberSettlement',context:context(settlementContext)}):bind(io('memberClose',{id:p('id')}),
      direct('determined',{host:p('host'),id:p('id'),workspace:p('workspace'),phase:p('phase'),closingSignal:p('closingSignal'),commitSignal:p('commitSignal'),
       status:cond(cancellation,text('stopped'),mr(pat('value',1),pat(),cond(eqStr(pat('error'),text('deadline')),text('timed_out'),text('failed'))))}))))))
 await s.create('isId',{target:int,item:E},bool,eqInt(A(p('item'),'id'),p('target')),[])
 await s.create('get',{state:S,target:int},option(E),first(filter(T(p('state'),'entries'),'isId',{target:p('target')})),[])
 await s.create('settled',{item:E},bool,matchOption(first(poll(p('item'))),matchOption(pat(),boolean(true),boolean(false)),boolean(false)),[])
 await s.create('capacity',{acc:int,item:E},int,cond(direct('settled',{item:p('item')}),p('acc'),add(p('acc'),integer(1))),[])
 await s.create('markSuperseded',{target:int,item:E},E,cond(eqInt(A(p('item'),'id'),p('target')),update(p('item'),'Entry',{superseded:boolean(true)}),p('item')),[])
 await s.create('markDiscarded',{workspace:string,item:E},E,cond(eqStr(A(p('item'),'workspace'),p('workspace')),update(p('item'),'Entry',{discarded:boolean(true)}),p('item')),[])
 await s.create('rejected',{host:cap('Host'),state:S,label:string,reason:string},S,bind(io('reply',{label:p('label'),record:concat(text('reject:'),p('reason'))}),p('state')))
 const reject=reason=>direct('rejected',{host:p('host'),state:p('state'),label:p('label'),reason:text(reason)})
 await s.create('register',{host:cap('Host'),state:S,workspace:string,base:string,previous:int,phase:cell,commitSignal:cell,target:handle,label:string},S,
  bind(io('reply',{label:p('label'),record:concat(text('spawn:'),num(T(p('state'),'next')))}),
   state(cat(map(T(p('state'),'entries'),'markSuperseded',{target:p('previous')}),L(E,construct('Entry',{id:T(p('state'),'next'),workspace:p('workspace'),base:p('base'),previous:p('previous'),discarded:boolean(false),superseded:boolean(false),phase:p('phase'),commitSignal:p('commitSignal'),handle:p('target'),...extension?.entryValues}))),add(T(p('state'),'next'),integer(1)),T(p('state'),'closing'),T(p('state'),'closingSignal'))))
 await s.create('launch',{host:cap('Host'),scope,state:S,workspace:string,base:string,previous:int,phase:cell,commitSignal:cell,label:string,deadline:int},S,
  bind(start('worker',T(p('state'),'next'),{host:p('host'),workspace:p('workspace'),previous:p('previous'),phase:p('phase'),commitSignal:p('commitSignal'),deadline:p('deadline'),closingSignal:T(p('state'),'closingSignal')}),
   direct('register',{host:p('host'),state:p('state'),workspace:p('workspace'),base:p('base'),previous:p('previous'),phase:p('phase'),commitSignal:p('commitSignal'),target:pat(),label:p('label')})))
 await s.create('admit',{host:cap('Host'),scope,state:S,workspace:string,base:string,previous:int,label:string,deadline:int},S,
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:string},bind({kind:'oneShotCreate',scope:p('scope'),valueType:string},direct('launch',{host:p('host'),scope:p('scope'),state:p('state'),workspace:p('workspace'),base:p('base'),previous:p('previous'),phase:pat('value',1),commitSignal:pat(),label:p('label'),deadline:p('deadline')}))))
 const admit=(workspace,base,previous)=>direct('admit',{host:p('host'),scope:p('scope'),state:p('state'),workspace,base,previous,label:p('label'),deadline:p('deadline')})
 const continueBody=
  cond(A(p('prior'),'superseded'),reject('superseded'),cond(A(p('prior'),'discarded'),reject('discarded'),
   cond(direct('settled',{item:p('prior')}),admit(A(p('prior'),'workspace'),A(p('prior'),'base'),A(p('prior'),'id')),reject('running'))))
 await s.create('continue',{host:cap('Host'),scope,state:S,prior:E,label:string,deadline:int},S,extension?extension.continueBody(continueBody,reject):continueBody)
 await s.create('spawn',{host:cap('Host'),scope,state:S,previous:int,label:string,deadline:int},S,
  cond(T(p('state'),'closing'),reject('closing'),cond(gt(fold(T(p('state'),'entries'),integer(0),'capacity',{},true),integer(1)),reject('capacity'),
   cond(eqInt(p('previous'),integer(0)),
    mr(io('workspaceCreate',{workspace:concat(text('w'),num(T(p('state'),'next'))),base:text('base')}),admit(concat(text('w'),num(T(p('state'),'next'))),text('base'),integer(0)),reject('workspace')),
    matchOption(direct('get',{state:p('state'),target:p('previous')}),direct('continue',{host:p('host'),scope:p('scope'),state:p('state'),prior:pat(),label:p('label'),deadline:p('deadline')}),reject('unknown'))))))
 // Inspect exposes the determined status; wait exposes only settled task results.
 await s.create('renderFinal',{entry:E,final:named('Final')},string,
  concat(num(A(p('entry'),'id')),text(','),F(p('final'),'status'),text(','),A(p('entry'),'workspace'),text(','),A(p('entry'),'base'),text(','),num(A(p('entry'),'previous')),text(','),cond(A(p('entry'),'discarded'),text('true'),text('false')),text(','),F(p('final'),'commit'),text(','),F(p('final'),'checks'),...(extension?[text(','),extension.renderSuffix(p('entry'))]:[]),text(';')),[])
 await s.create('inspectPending',{entry:E},string,
  bind({kind:'oneShotWait',cell:A(p('entry'),'commitSignal'),limit:some(integer(0))},bind(phase(p('entry')),
   direct('renderFinal',{entry:p('entry'),final:final(matchOption(pat(),pat(),text('running')),matchOption(pat('value',1),pat(),text('')))}))),[])
 await s.create('render',{entry:E,mode:string},string,
  matchOption(first(poll(p('entry'))),matchOption(pat(),mr(pat(),direct('renderFinal',{entry:p('entry'),final:pat()}),text('unexpected result;')),
   cond(eqStr(p('mode'),text('inspect')),direct('inspectPending',{entry:p('entry')}),concat(num(A(p('entry'),'id')),text(',running;')))),text('invalid;')),[])
 await s.create('renderFold',{acc:string,item:E,mode:string},string,concat(p('acc'),direct('render',{entry:p('item'),mode:p('mode')})),[])
 await s.create('handleOf',{item:E},handle,A(p('item'),'handle'),[])
 await s.create('selected',{state:S,target:int},entries,cond(eqInt(p('target'),integer(0)),T(p('state'),'entries'),filter(T(p('state'),'entries'),'isId',{target:p('target')})),[])
 await s.create('view',{host:cap('Host'),state:S,target:int,mode:string,label:string},S,
  bind(io('reply',{label:p('label'),record:fold(direct('selected',{state:p('state'),target:p('target')}),text(''),'renderFold',{mode:p('mode')},true)}),p('state')))
 await s.create('wait',{host:cap('Host'),state:S,target:int,mode:string,limit:int,label:string},S,
  bind(cond(eqStr(p('mode'),text('any')),wait(map(direct('selected',{state:p('state'),target:p('target')}),'handleOf'),'any',p('limit')),wait(map(direct('selected',{state:p('state'),target:p('target')}),'handleOf'),'all',p('limit'))),
   direct('view',{host:p('host'),state:p('state'),target:p('target'),mode:text('wait'),label:p('label')})))
 await s.create('stopEntry',{host:cap('Host'),entry:E},unit,
  bind(matchOption(phase(p('entry')),unitValue,{kind:'taskCancel',task:A(p('entry'),'handle')}),
   bind(io('gate',{label:concat(text('stop-join:'),num(A(p('entry'),'id')))}),bind(wait(L(handle,A(p('entry'),'handle'))),unitValue))))
 await s.create('stop',{host:cap('Host'),state:S,target:int,label:string},S,
  matchOption(direct('get',{state:p('state'),target:p('target')}),bind(direct('stopEntry',{host:p('host'),entry:pat()}),direct('view',{host:p('host'),state:p('state'),target:p('target'),mode:text('inspect'),label:p('label')})),reject('unknown')))
 await s.create('discardEntry',{host:cap('Host'),state:S,entry:E,label:string},S,
  cond(A(p('entry'),'superseded'),reject('superseded'),cond(A(p('entry'),'discarded'),reject('discarded'),cond(direct('settled',{item:p('entry')}),
   mr(io('workspaceDiscard',{workspace:A(p('entry'),'workspace')}),bind(io('reply',{label:p('label'),record:text('discarded')}),update(p('state'),'State',{entries:map(T(p('state'),'entries'),'markDiscarded',{workspace:A(p('entry'),'workspace')})})),reject('discard effect')),reject('running')))))
 await s.create('discard',{host:cap('Host'),state:S,target:int,label:string},S,
  matchOption(direct('get',{state:p('state'),target:p('target')}),direct('discardEntry',{host:p('host'),state:p('state'),entry:pat(),label:p('label')}),reject('unknown')))
 await s.create('stopFold',{host:cap('Host'),acc:unit,item:E},unit,bind(direct('stopEntry',{host:p('host'),entry:p('item')}),unitValue))
 await s.create('finish',{host:cap('Host'),state:S,label:string},S,
  cond(T(p('state'),'closing'),bind(io('reply',{label:p('label'),record:text('finished')}),p('state')),
   bind({kind:'oneShotComplete',cell:T(p('state'),'closingSignal'),value:unitValue},
    bind(fold(T(p('state'),'entries'),unitValue,'stopFold',{host:p('host')},true),update(p('state'),'State',{closing:boolean(true)})))))
 if(extension)await extension.define(s)
 const args={host:p('host'),scope:p('scope'),state:p('state')}
 const req=p('request'), label=C(req,'label'), target=C(req,'target')
 const dispatch=cond(eqStr(C(req,'op'),text('spawn')),direct('spawn',{...args,previous:C(req,'previous'),label,deadline:C(req,'limit')}),
  cond(eqStr(C(req,'op'),text('wait')),direct('wait',{host:p('host'),state:p('state'),target,mode:C(req,'mode'),limit:C(req,'limit'),label}),
  cond(eqStr(C(req,'op'),text('inspect')),direct('view',{host:p('host'),state:p('state'),target,mode:text('inspect'),label}),
  cond(eqStr(C(req,'op'),text('stop')),direct('stop',{host:p('host'),state:p('state'),target,label}),
  cond(eqStr(C(req,'op'),text('discard')),direct('discard',{host:p('host'),state:p('state'),target,label}),
  cond(eqStr(C(req,'op'),text('finish')),direct('finish',{host:p('host'),state:p('state'),label}),
  cond(eqStr(C(req,'op'),text('gate')),bind(io('gate',{label}),p('state')),direct('rejected',{host:p('host'),state:p('state'),label,reason:text('operation')}))))))))
 await s.create('dispatch',{...{host:cap('Host'),scope,state:S},request:Q},S,extension?extension.dispatch(dispatch,{args,req,label,target}):dispatch)
 const dispatchCall=direct('dispatch',{host:p('host'),scope:p('scope'),state:p('acc'),request:p('item')})
 await s.create('requestFold',{host:cap('Host'),scope,acc:S,item:Q},S,extension?bind(dispatchCall,direct('auditState',{host:p('host'),state:pat(),label:C(p('item'),'label')})):dispatchCall)
 await s.create('foldController',{host:cap('Host'),scope,requests,closingSignal:closingCell},result(S),
  bind(fold(p('requests'),state(L(E),integer(1),boolean(false),p('closingSignal')),'requestFold',{host:p('host'),scope:p('scope')},true),
   cond(T(pat(),'closing'),ok(pat()),bind(direct('finish',{host:p('host'),state:pat(),label:text('auto-finish')}),ok(pat())))))
 await s.create('controller',{host:cap('Host'),scope,requests},result(S),
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:unit},direct('foldController',{host:p('host'),scope:p('scope'),requests:p('requests'),closingSignal:pat()})))
 await s.create('resourceBody',{host:cap('Host'),requests,handle:unit},result(S),
  {kind:'taskScope',body:'controller',context:context({host:p('host'),requests:p('requests')})})
 await s.create('isFinish',{item:Q},bool,eqStr(C(p('item'),'op'),text('finish')),[])
 await s.create('split',{requests},int,
  matchOption({kind:'listFindIndex',source:p('requests'),function:'isFinish'},add(pat(),integer(1)),{kind:'listLength',value:p('requests')}),[])
 await s.create('finishLabel',{requests},string,matchOption(first(filter(p('requests'),'isFinish')),C(pat(),'label'),text('auto-finish')),[])
 await s.create('closedController',{host:cap('Host'),scope,state:S,requests},result(unit),
  bind(fold(p('requests'),p('state'),'requestFold',{host:p('host'),scope:p('scope')},true),ok(unitValue)))
 await s.create('afterRelease',{host:cap('Host'),state:S,requests},result(unit),
  bind(io('reply',{label:direct('finishLabel',{requests:p('requests')}),record:text('finished')}),
   {kind:'taskScope',body:'closedController',context:context({host:p('host'),state:p('state'),requests:{kind:'listSlice',list:p('requests'),start:direct('split',{requests:p('requests')}),end:{kind:'listLength',value:p('requests')}}})}))
 await s.create('run',{host:cap('Host'),requests},result(unit),
  mr({kind:'useResource',acquire:io('open'),releaseCapability:p('host'),capability:'Host',releaseOperation:'close',body:'resourceBody',context:context({host:p('host'),requests:{kind:'listSlice',list:p('requests'),start:integer(0),end:direct('split',{requests:p('requests')})}})},
   direct('afterRelease',{host:p('host'),state:pat(),requests:p('requests')}),err(pat('error'),unit)))
 // Separate negative capability probe: cancel a caller's wait without cancelling
 // its Member. Handles remain entirely in these graph functions.
 await s.create('callerWaiter',{host:cap('Host'),target:task(result(string)),item:unit},result(unit),
  bind(io('gate',{label:text('caller-wait-entered')}),bind(wait(L(task(result(string)),p('target'))),ok(unitValue))))
 await s.create('cancelCaller',{host:cap('Host'),member:task(result(string)),caller:task(result(unit))},result(unit),
  bind(io('gate',{label:text('await-caller-wait')}),
   bind({kind:'taskCancel',task:p('caller')},
    bind(wait(L(task(result(unit)),p('caller')),'all',integer(0)),
     bind(io('reply',{label:text('caller-cancellation'),record:matchOption(first(pat()),matchOption(pat(),text('caller:return'),text('caller:running')),text('invalid'))}),
      bind(io('reply',{label:text('member-alive'),record:matchOption(first(wait(L(task(result(string)),p('member')),'all',integer(0))),matchOption(pat(),text('member:finished'),text('member:running')),text('invalid'))}),
       bind({kind:'taskCancel',task:p('member')},bind(wait(L(task(result(string)),p('member'))),bind(wait(L(task(result(unit)),p('caller'))),ok(unitValue))))))))))
 await s.create('startCaller',{host:cap('Host'),scope,member:task(result(string))},result(unit),
  bind(start('callerWaiter',unitValue,{host:p('host'),target:p('member')}),direct('cancelCaller',{host:p('host'),member:p('member'),caller:pat()})))
 await s.create('callerScope',{host:cap('Host'),scope},result(unit),
  bind(start('providerTask',integer(1),{host:p('host'),workspace:text('w1'),previous:integer(0)}),direct('startCaller',{host:p('host'),scope:p('scope'),member:pat()})))
 await s.create('callerResource',{host:cap('Host'),handle:unit},result(unit),{kind:'taskScope',body:'callerScope',context:context({host:p('host')})})
 await s.create('callerCancellationRun',{host:cap('Host')},result(unit),
  {kind:'useResource',acquire:io('open'),releaseCapability:p('host'),capability:'Host',releaseOperation:'close',body:'callerResource',context:context({host:p('host')})})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,nodes:s.nodes}
}
