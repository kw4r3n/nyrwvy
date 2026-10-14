import {author as boundedAuthor,maxFrameBytes} from '../offline-session-bounded-input/author.mjs'
import {cap,scope,bool,unit,unitValue,result,named,oneShot,task,p,ok,direct,letIn,some,integer,resolveFrames,L,ctx,pat} from '../offline-leader-jsonrpc/recipes.mjs'
export {maxFrameBytes}
const F=named('InputFrame'),C=oneShot(F),W=named('AdmissionOwner'),R=result(unit),D=result(W)

// Reader, dispatcher and observer belong to the existing execution scope.
// Its body observes either task concurrently with command dispatch. A reader
// exception exits that body, so TaskScope cancels/drains all work and preserves
// that exception; the inherited determined mutation shields remain intact.
export async function author(s,{deferred=false}={}){
 const inherited=await boundedAuthor(s)
 if(deferred)return inherited
 const driver={host:cap('Host'),scope,cell:C,reader:task(R),owner:W,wrongBoundary:bool}
 await s.create('inputDispatchOwned',{...driver,item:unit},D,
  ok(direct('inputDispatch',Object.fromEntries(Object.keys(driver).map(k=>[k,p(k)])))))
 const wait=(t,type)=>({kind:'taskWait',tasks:L(task(type),t),mode:'all'})
 await s.create('inputReaderAwait',{reader:task(R),owner:W,item:unit},D,
  resolveFrames(letIn(wait(p('reader'),R),()=>ok(p('owner')))),[])
 const owner=p('owner')
 await s.publish(await s.call('replace',{function:'ownedTransport',replacement:resolveFrames(
  letIn({kind:'oneShotCreate',scope:p('scope'),valueType:F},cell=>
   letIn({kind:'taskStart',scope:p('scope'),worker:'inputReader',item:unitValue,context:ctx({transport:p('transport'),scope:p('scope'),tail:cell})},reader=>
    letIn({kind:'taskStart',scope:p('scope'),worker:'inputDispatchOwned',item:unitValue,context:ctx({host:p('host'),scope:p('scope'),cell,reader,owner,wrongBoundary:p('wrongBoundary')})},dispatcher=>
     letIn({kind:'taskStart',scope:p('scope'),worker:'inputReaderAwait',item:unitValue,context:ctx({reader,owner})},observer=>
      letIn({kind:'taskWait',tasks:L(task(D),observer,dispatcher),mode:'any'},()=>
       some({kind:'listItemAt',list:wait(dispatcher,D),index:integer(0)},entry=>
        some(entry,value=>({kind:'matchResult',input:value,okBody:pat(),errBody:owner}),owner),owner))))))) }))
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,declarations,nodes:s.nodes}
}
