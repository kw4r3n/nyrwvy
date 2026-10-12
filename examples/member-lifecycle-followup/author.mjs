// Two example-only compositions. Original graph functions are retained unchanged.
import {author as baseline} from '../member-lifecycle/author.mjs'
import {string,int,unit,cap,scope,task,oneShot,list,option,result,named,p,text,integer,unitValue,pat,bind,field,construct,ok,err,direct,cond,matchOption,eqStr,invoke,cancellation,concat} from '../dynamic-worker-collector/lib.mjs'
const fields=o=>Object.entries(o).map(([displayName,type])=>({displayName,type}))
const ctx=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
const E=named('Entry'),R=result(named('Final')),members=list(E),tasks=list(task(R)),states=list(option(R)),C=result(states),signal=oneShot(states),O=named('ProviderObservation')
const io=(operation,arguments_={})=>invoke(p('host'),'Host',operation,arguments_)
const mr=(input,okBody,errBody)=>({kind:'matchResult',input,okBody,errBody})
const L=(type,...items)=>({kind:'listConstruct',itemType:type,items})
const some=value=>({kind:'optionSome',value}),none=type=>({kind:'optionNone',itemType:type})
const wait=(values,mode='all',limit)=>({kind:'taskWait',tasks:values,mode,...(limit?{limit}: {})})
const first=values=>({kind:'listItemAt',list:values,index:integer(0)})
const map=(source,fn,context={})=>({kind:'listMap',source,function:fn,context:ctx(context)})
const fold=(source,seed,fn,context={})=>({kind:'listEffectFold',source,seed,function:fn,context:ctx(context)})
const A=(source,name)=>field(source,'Entry',name)
const F=(source,name)=>field(source,'Final',name)
const P=(source,name)=>field(source,'ProviderObservation',name)
const start=(worker,item,context)=>({kind:'taskStart',scope:p('scope'),worker,item,context:ctx(context)})
const equal=(parameter,value)=>eqStr(p(parameter),text(value))
const or=(left,right)=>({kind:'boolOr',left,right})
const ni=x=>({kind:'int64ToString',value:x})
const stopMembers=()=>fold(p('members'),unitValue,'followupStop',{host:p('host')})
const handles=()=>map(p('members'),'handleOf')
const observed=()=>direct('followupRecords',{host:p('host'),members:p('members'),label:text('observed')})

export async function author(s){
 await baseline(s)
 await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[{displayName:'ProviderObservation',typeKind:{kind:'product',fields:fields({kind:string,cause:string})}}]}}))
 await s.publish(await s.call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'TaggedProvider',operations:[{displayName:'run',parameters:fields({id:int,workspace:string}),resultType:result(O),effect:'Io'}]}}))
 await s.create('followupLease',{provider:cap('TaggedProvider'),workspace:string,handle:int},result(O),
  cond(cancellation,err(text('cancelled'),O),invoke(p('provider'),'TaggedProvider','run',{id:p('handle'),workspace:p('workspace')})))
 const determine=status=>direct('determined',{host:p('host'),id:p('id'),workspace:p('workspace'),phase:p('phase'),commitSignal:p('commitSignal'),closingSignal:p('closingSignal'),status})
 await s.create('followupDetermine',{host:cap('Host'),id:int,workspace:string,phase:oneShot(string),commitSignal:oneShot(string),closingSignal:oneShot(unit),observation:result(O)},R,
  cond(cancellation,determine(text('stopped')),mr(p('observation'),
   cond(eqStr(P(pat(),'kind'),text('completed')),determine(text('completed')),
    bind(io('reply',{label:concat(text('provider-evidence:'),ni(p('id'))),record:concat(P(pat(),'kind'),text('|'),P(pat(),'cause'))}),determine(text('failed')))),determine(text('failed')))))
 await s.create('followupWorker',{host:cap('Host'),provider:cap('TaggedProvider'),workspace:string,phase:oneShot(string),commitSignal:oneShot(string),closingSignal:oneShot(unit),id:int},R,
  bind({kind:'useResource',acquire:ok(p('id')),releaseCapability:p('host'),capability:'Host',releaseOperation:'memberClose',body:'followupLease',context:ctx({provider:p('provider'),workspace:p('workspace')})},
   direct('followupDetermine',{host:p('host'),id:p('id'),workspace:p('workspace'),phase:p('phase'),commitSignal:p('commitSignal'),closingSignal:p('closingSignal'),observation:pat()})))
 await s.create('followupEntry',{id:int,workspace:string,phase:oneShot(string),commitSignal:oneShot(string),handle:task(R)},E,
  construct('Entry',{id:p('id'),workspace:p('workspace'),base:text('base'),previous:integer(0),discarded:{kind:'boolLiteral',value:false},superseded:{kind:'boolLiteral',value:false},phase:p('phase'),commitSignal:p('commitSignal'),handle:p('handle')}),[])
 await s.create('followupLaunch',{host:cap('Host'),provider:cap('TaggedProvider'),scope,closingSignal:oneShot(unit),phase:oneShot(string),commitSignal:oneShot(string),id:int},E,
  bind(start('followupWorker',p('id'),{host:p('host'),provider:p('provider'),workspace:concat(text('w'),ni(p('id'))),phase:p('phase'),commitSignal:p('commitSignal'),closingSignal:p('closingSignal')}),
   direct('followupEntry',{id:p('id'),workspace:concat(text('w'),ni(p('id'))),phase:p('phase'),commitSignal:p('commitSignal'),handle:pat()})))
 await s.create('followupAdmit',{host:cap('Host'),provider:cap('TaggedProvider'),scope,closingSignal:oneShot(unit),id:int},E,
  bind(io('workspaceCreate',{workspace:concat(text('w'),ni(p('id'))),base:text('base')}),
   bind({kind:'oneShotCreate',scope:p('scope'),valueType:string},bind({kind:'oneShotCreate',scope:p('scope'),valueType:string},
    direct('followupLaunch',{host:p('host'),provider:p('provider'),scope:p('scope'),closingSignal:p('closingSignal'),phase:pat('value',1),commitSignal:pat(),id:p('id')})))))
 await s.create('followupPublish',{host:cap('Host'),signal,states},C,
  bind({kind:'oneShotComplete',cell:p('signal'),value:p('states')},
   bind(io('reply',{label:text('monitor-delivery'),record:cond(pat(),text('accepted'),text('closed'))}),bind(io('gate',{label:text('monitor-published')}),ok(p('states'))))))
 await s.create('followupMonitor',{host:cap('Host'),members,signal,mode:string,limit:int,item:unit},C,
  bind(io('gate',{label:text('monitor-entered')}),
   bind(cond(equal('mode','any'),wait(handles(),'any',cond({kind:'int64LessThan',left:p('limit'),right:integer(0)},none(int),some(p('limit')))),
    wait(handles(),'all',cond({kind:'int64LessThan',left:p('limit'),right:integer(0)},none(int),some(p('limit'))))),
    direct('followupPublish',{host:p('host'),signal:p('signal'),states:pat()}))))
 await s.create('followupCaller',{host:cap('Host'),signal,item:unit},C,
  bind(io('gate',{label:text('caller-entered')}),
   matchOption({kind:'oneShotWait',cell:p('signal')},
    bind(io('gate',{label:text('caller-observed')}),cond(cancellation,err(text('caller cancelled'),states),ok(pat('value',1)))),err(text('caller cancelled'),states))))
 await s.create('followupSnapshotText',{states,index:int,log:string},string,
  matchOption({kind:'listItemAt',list:p('states'),index:p('index')},
   bind(concat(ni({kind:'int64WrappingAdd',left:p('index'),right:integer(1)}),text(','),matchOption(pat(),mr(pat(),F(pat(),'status'),text('error')),text('running')),text(';')),
    {...direct('followupSnapshotText',{index:{kind:'int64WrappingAdd',left:p('index'),right:integer(1)},log:concat(p('log'),pat())}),keepUnlisted:true}),p('log')),[])
 await s.create('followupCallerRecord',{host:cap('Host'),caller:task(C)},unit,
  matchOption(first(wait(L(task(C),p('caller')))),matchOption(pat(),mr(pat(),
   bind(io('reply',{label:text('observer-payload'),record:direct('followupSnapshotText',{states:pat(),index:integer(0),log:text('')})}),io('reply',{label:text('caller'),record:text('caller:observed')})),
   io('reply',{label:text('caller'),record:text('caller:cancelled')})),io('reply',{label:text('caller'),record:text('caller:pending')})),unitValue))
 await s.create('followupRecords',{host:cap('Host'),members,label:string},unit,
  io('reply',{label:p('label'),record:fold(p('members'),text(''),'renderFold',{mode:text('wait')})}))
 await s.create('followupStop',{host:cap('Host'),acc:unit,item:E},unit,bind(direct('stopEntry',{host:p('host'),entry:p('item')}),unitValue))
 await s.create('followupFinish',{host:cap('Host'),members,monitor:task(C)},result(members),
  bind(stopMembers(),bind(wait(L(task(C),p('monitor'))),ok(p('members')))))
 await s.create('followupProgress',{monitor:task(C),caller:task(C)},unit,
  bind(wait(L(task(C),p('monitor'),p('caller')),'any'),unitValue),[])
 await s.create('followupAfterCaller',{host:cap('Host'),members,monitor:task(C),caller:task(C),mode:string},result(members),
  bind(direct('followupCallerRecord',{host:p('host'),caller:p('caller')}),
   bind(observed(),bind(io('reply',{label:text('occupied'),record:ni(fold(p('members'),integer(0),'capacity'))}),
    bind(cond(equal('mode','caller-cancel'),io('reply',{label:text('monitor'),record:matchOption(first(wait(L(task(C),p('monitor')),'all',some(integer(0)))),matchOption(pat(),text('monitor:finished'),text('monitor:running')),text('invalid'))}),unitValue),
     bind(cond(equal('mode','snapshot-timeout'),io('gate',{label:text('release-snapshot')}),unitValue),
      bind(cond(or(equal('mode','tagged-isolation'),equal('mode','tagged-expected')),bind(io('gate',{label:text('release-peer')}),bind(wait(handles()),unitValue)),unitValue),
       direct('followupFinish',{host:p('host'),members:p('members'),monitor:p('monitor')}))))))))
 const cancelCaller=()=>bind({kind:'taskCancel',task:p('caller')},unitValue)
 const release=label=>io('gate',{label:text(label)})
 const callerFinished=()=>bind(wait(L(task(C),p('caller'))),unitValue)
 const progress=()=>direct('followupProgress',{monitor:p('monitor'),caller:p('caller')})
 await s.create('followupCoordinate',{host:cap('Host'),members,monitor:task(C),caller:task(C),mode:string},result(members),
  bind(release('waiter-ready'),
   cond(or(equal('mode','global-exit'),equal('mode','global-external')),bind(release('global-arm'),ok(p('members'))),
   bind(cond(or(equal('mode','caller-cancel'),equal('mode','race-cancel-first')),cancelCaller(),
    cond(equal('mode','race-complete-first'),bind(release('release-all'),bind(progress(),bind(callerFinished(),cancelCaller()))),
     cond(equal('mode','race-published-cancel'),bind(release('release-all'),bind(progress(),bind(release('observation-ready'),bind(cancelCaller(),release('release-caller'))))),
      cond(or(equal('mode','any'),or(equal('mode','tagged-isolation'),equal('mode','tagged-expected'))),release('release-first'),
       cond(equal('mode','all'),release('release-all'),unitValue))))),
    bind(cond(equal('mode','race-cancel-first'),bind(callerFinished(),bind(release('release-all'),bind(wait(handles()),unitValue))),unitValue),
     bind(progress(),direct('followupAfterCaller',{host:p('host'),members:p('members'),monitor:p('monitor'),caller:p('caller'),mode:p('mode')})))))))
 await s.create('followupStartCaller',{host:cap('Host'),scope,members,signal,monitor:task(C),mode:string},result(members),
  bind(start('followupCaller',unitValue,{host:p('host'),signal:p('signal')}),
   direct('followupCoordinate',{host:p('host'),members:p('members'),monitor:p('monitor'),caller:pat(),mode:p('mode')})))
 await s.create('followupStartMonitor',{host:cap('Host'),scope,members,signal,mode:string,limit:int},result(members),
  bind(start('followupMonitor',unitValue,{host:p('host'),members:p('members'),signal:p('signal'),mode:cond(or(equal('mode','any'),or(equal('mode','tagged-isolation'),equal('mode','tagged-expected'))),text('any'),text('all')),limit:p('limit')}),
   direct('followupStartCaller',{host:p('host'),scope:p('scope'),members:p('members'),signal:p('signal'),monitor:pat(),mode:p('mode')})))
 await s.create('followupScenario',{host:cap('Host'),scope,members,mode:string,limit:int},result(members),
  bind(io('gate',{label:text('members-ready')}),
    bind(cond(equal('mode','snapshot-timeout'),bind(io('gate',{label:text('release-first')}),io('gate',{label:text('snapshot1')})),unitValue),
     bind({kind:'oneShotCreate',scope:p('scope'),valueType:states},direct('followupStartMonitor',{host:p('host'),scope:p('scope'),members:p('members'),signal:pat(),mode:p('mode'),limit:p('limit')})))))
 await s.create('followupMembers',{host:cap('Host'),provider:cap('TaggedProvider'),scope,closingSignal:oneShot(unit),mode:string,limit:int},result(members),
  bind(direct('followupAdmit',{host:p('host'),provider:p('provider'),scope:p('scope'),closingSignal:p('closingSignal'),id:integer(1)}),
   bind(direct('followupAdmit',{host:p('host'),provider:p('provider'),scope:p('scope'),closingSignal:p('closingSignal'),id:integer(2)}),
    direct('followupScenario',{host:p('host'),scope:p('scope'),members:L(E,pat('value',1),pat()),mode:p('mode'),limit:p('limit')}))))
 await s.create('followupScope',{host:cap('Host'),provider:cap('TaggedProvider'),scope,mode:string,limit:int},result(members),
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:unit},direct('followupMembers',{host:p('host'),provider:p('provider'),scope:p('scope'),closingSignal:pat(),mode:p('mode'),limit:p('limit')})))
 await s.create('followupResource',{host:cap('Host'),provider:cap('TaggedProvider'),mode:string,limit:int,handle:unit},result(unit),
  mr({kind:'taskScope',body:'followupScope',context:ctx({host:p('host'),provider:p('provider'),mode:p('mode'),limit:p('limit')})},
   bind(direct('followupRecords',{host:p('host'),members:pat(),label:text('final')}),
    cond(or(cancellation,or(equal('mode','global-exit'),equal('mode','global-external'))),err(text('global cancelled'),unit),ok(unitValue))),err(pat('error'),unit)))
 await s.create('followupRun',{host:cap('Host'),provider:cap('TaggedProvider'),mode:string,limit:int},result(unit),
  {kind:'useResource',acquire:io('open'),releaseCapability:p('host'),capability:'Host',releaseOperation:'close',body:'followupResource',context:ctx({host:p('host'),provider:p('provider'),mode:p('mode'),limit:p('limit')})})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,nodes:s.nodes}
}
