// The subject is the unchanged admission composition. This harness owns it as
// one task so TaskCancel can inject the same linked parent cancellation in the
// virtual clock. Real runs also inject a root CancellationToken externally.
import {author as admission} from '../member-concurrency/author.mjs'
import {unit,unitValue,cap,scope,task,list,result,named,p,pat,bind,direct,invoke,text,integer,ok,err,cond,cancellation,matchOption} from '../dynamic-worker-collector/lib.mjs'
const R=result(unit),ctx=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
const io=label=>invoke(p('host'),'Host','gate',{label:text(label)})
export async function author(s,{adapted=true,recoverCompleted=true,protectDetermined=true,retainCloseIntent=true,containSnapshotFaults=false}={}){
 await admission(s,{recoverCompleted,protectDetermined,retainCloseIntent,containSnapshotFaults})
 // Check the restored owner token only after concurrentRun has drained its
 // scope and released its resource. Raw drain/release faults still propagate.
 if(adapted)await s.create('ownerCancellationRun',{host:cap('Host'),requests:list(named('Request'))},R,
  bind(direct('concurrentRun',{host:p('host'),requests:p('requests')}),cond(cancellation,err(text('owner cancelled'),unit),pat())))
 await s.create('ownerProbeWorker',{host:cap('Host'),requests:list(named('Request')),item:unit},R,direct(adapted?'ownerCancellationRun':'concurrentRun',{host:p('host'),requests:p('requests')}))
 await s.create('ownerProbeJoin',{host:cap('Host'),owner:task(R)},R,
  bind(io('probe-ready'),bind({kind:'taskCancel',task:p('owner')},bind(io('probe-cancelled'),bind(io('probe-release'),
   matchOption({kind:'listItemAt',list:{kind:'taskWait',tasks:{kind:'listConstruct',itemType:task(R),items:[p('owner')]},mode:'all'},index:integer(0)},matchOption(pat(),pat(),ok(unitValue)),ok(unitValue)))))))
 await s.create('ownerProbeScope',{host:cap('Host'),requests:list(named('Request')),scope},R,
  bind({kind:'taskStart',scope:p('scope'),worker:'ownerProbeWorker',item:unitValue,context:ctx({host:p('host'),requests:p('requests')})},direct('ownerProbeJoin',{host:p('host'),owner:pat()})))
 await s.create('ownerProbe',{host:cap('Host'),requests:list(named('Request'))},R,{kind:'taskScope',body:'ownerProbeScope',context:ctx({host:p('host'),requests:p('requests')})})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,nodes:s.nodes}
}
