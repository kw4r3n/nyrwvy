// Generic fault handling composed with existing cleanup, scopes and cancellation.
import {define as shieldDefinitions} from '../cleanup-shield/author.mjs'
import {cap,unit,string,unitValue,p,text,pat,bind,ok,err,result,invoke,direct,cancellation,cond} from '../dynamic-worker-collector/lib.mjs'
const host=cap('Probe'),R=result(string),exception={kind:'builtinGeneric',generic:'exception',arguments:[]}
const ctx=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
const io=(operation,args={})=>invoke(p('host'),'Probe',operation,args)
const shield=(body,values)=>({kind:'cancellationShield',body,context:ctx(values)})
const catcher=(body,bodyValues,handler,handlerValues)=>({kind:'exceptionCatch',body,bodyContext:ctx(bodyValues),handler,handlerContext:ctx(handlerValues)})
const rethrow=()=>({kind:'exceptionRethrow',value:p('exception'),resultType:R})
export async function author(s){
 await s.call('open',{mode:'create',snapshot:s.snapshot})
 await shieldDefinitions((name,params,type,body,effects=[])=>s.create(name,params,type,body,effects),a=>s.call('request',{op:a.op,arguments:Object.fromEntries(Object.entries(a).filter(([k])=>k!=='op'))}).then(p=>s.publish(p)))
 const create=(name,params,body)=>s.create(name,params,R,body)
 await create('faultBody',{host,item:unit},io('step',{phase:text('fault')}))
 await create('faultRethrow',{host,exception},bind(io('report',{message:text('caught')}),rethrow()))
 await create('faultRecover',{host,exception},bind(io('report',{message:cond(cancellation,text('restored-cancelled'),text('restored-clear'))}),err({kind:'exceptionMessage',value:p('exception')})))
 await create('faultCatch',{host,item:unit},catcher('faultBody',{host:p('host')},'faultRethrow',{host:p('host')}))
 await create('faultNested',{host,item:unit},shield('faultCatch',{host:p('host')}))
 await create('faultOuter',{host},catcher('faultNested',{host:p('host')},'faultRecover',{host:p('host')}))
 // Cleanup nested under the protected tail drains its own worker and releases before rethrow.
 await create('faultCleanup',{host,exception},bind(shield('ownedBody',{host:p('host')}),rethrow()))
 await create('faultOwned',{host,item:unit},catcher('faultBody',{host:p('host')},'faultCleanup',{host:p('host')}))
 await create('faultOwnedShield',{host},shield('faultOwned',{host:p('host')}))
 await create('faultResourceBody',{host,handle:unit},direct('faultCatch',{host:p('host'),item:unitValue}))
 await create('faultResource',{host,item:unit},{kind:'useResource',acquire:io('open'),releaseCapability:p('host'),capability:'Probe',releaseOperation:'close',body:'faultResourceBody',context:ctx({host:p('host')})})
 await create('faultResourceShield',{host},shield('faultResource',{host:p('host')}))
 await create('typedAbort',{item:unit},{kind:'abort',error:text('typed abort')})
 await create('abortCatch',{host},catcher('typedAbort',{},'faultRecover',{host:p('host')}))
 await create('contextFault',{host},catcher('faultBody',{host:bind(io('step',{phase:text('context')}),p('host'))},'faultRecover',{host:p('host')}))
 await create('directCancel',{parent:{kind:'builtinGeneric',generic:'task',arguments:[R]}},bind({kind:'taskCancel',task:p('parent')},ok(text('cancel-issued'))))
 const d=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:d.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:d.capabilities.map(c=>c.id)}}})
 return {artifact,nodes:s.nodes,declarations:d}
}
