import {author as wireAuthor} from '../offline-leader-jsonrpc/author.mjs'
import {cap,scope,string,bool,unit,unitValue,result,ok,option,named,oneShot,task,p,invoke,direct,field,construct,boolean,text,integer,cond,cancellation,letIn,some,resolveFrames,L,ctx} from '../offline-leader-jsonrpc/recipes.mjs'

const F=named('InputFrame'),C=oneShot(F),A=oneShot(unit),W=named('AdmissionOwner'),R=result(unit)
const f=(value,key)=>field(value,'InputFrame',key)
const create=valueType=>({kind:'oneShotCreate',scope:p('scope'),valueType})
const complete=(cell,value)=>({kind:'oneShotComplete',cell,value})
const wait=(cell,limit)=>({kind:'oneShotWait',cell,...(limit===undefined?{}:{limit:{kind:'optionSome',value:integer(limit)}})})
const loop=(fn,args={})=>({...direct(fn,args),keepUnlisted:true})
const read=()=>invoke(p('transport'),'Transport','readLine')

// One reader task owns framing. The driver acknowledges a frame before folding
// it, allowing one following frame (or EOF) to be read while that fold blocks.
// All command policy, ordered admission and lifecycle remain graph functions.
export async function author(s){
 return wireAuthor(s,{sessionInput:{parameters:{transport:cap('Transport')},
  async define(s){
   const make=(name,ps,type,body,effects=['Io'])=>s.create(name,ps,type,resolveFrames(body),effects)
   await s.publish(await s.call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'Transport',operations:[{displayName:'readLine',parameters:[],resultType:option(string),effect:'Io'}]}}))
   await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[{displayName:'InputFrame',typeKind:{kind:'product',fields:Object.entries({ended:bool,line:string,next:C,ack:A}).map(([displayName,type])=>({displayName,type}))}}]}}))
   const reader={transport:cap('Transport'),scope,tail:C,item:unit}
   await make('inputReader',reader,R,ok(unitValue))
   await s.publish(await s.call('replace',{function:'inputReader',replacement:resolveFrames(
    some(read(),line=>letIn(create(F),next=>letIn(create(unit),ack=>
     letIn(complete(p('tail'),construct('InputFrame',{ended:boolean(false),line,next,ack})),()=>
      letIn(wait(ack),()=>loop('inputReader',{tail:next}))))),
     letIn(create(unit),ack=>letIn(complete(p('tail'),construct('InputFrame',{ended:boolean(true),line:text(''),next:p('tail'),ack})),()=>ok(unitValue))))) }))
   // A faulted reader must not leave the driver parked on an unresolved cell.
   // Timed cell observation plus a task snapshot surfaces that exact fault.
   const next={cell:C,reader:task(R)}
   await make('inputNext',next,option(F),{kind:'optionNone',itemType:F},[])
   await s.publish(await s.call('replace',{function:'inputNext',replacement:resolveFrames(
    some(wait(p('cell'),25),frame=>({kind:'optionSome',value:frame}),
     letIn({kind:'taskWait',tasks:L(task(R),p('reader')),mode:'all',limit:{kind:'optionSome',value:integer(0)}},()=>loop('inputNext'))))}))
   const driver={host:cap('Host'),scope,cell:C,reader:task(R),owner:W,wrongBoundary:bool}
   await make('inputDispatch',driver,W,p('owner'))
   await s.publish(await s.call('replace',{function:'inputDispatch',replacement:resolveFrames(
    cond(cancellation,
     letIn({kind:'taskWait',tasks:L(task(R),p('reader')),mode:'all'},()=>p('owner')),
     some(direct('inputNext',{cell:p('cell'),reader:p('reader')}),frame=>cond(f(frame,'ended'),
     letIn({kind:'taskWait',tasks:L(task(R),p('reader')),mode:'all'},()=>p('owner')),
     letIn(complete(f(frame,'ack'),unitValue),()=>
      letIn(direct('wireFold',{host:p('host'),scope:p('scope'),acc:p('owner'),item:f(frame,'line'),wrongBoundary:p('wrongBoundary')}),owner=>
       loop('inputDispatch',{cell:f(frame,'next'),owner})))),p('owner'))))}))
   await make('ownedTransport',{host:cap('Host'),transport:cap('Transport'),scope,owner:W,wrongBoundary:bool},W,
    letIn(create(F),cell=>letIn({kind:'taskStart',scope:p('scope'),worker:'inputReader',item:unitValue,context:ctx({transport:p('transport'),scope:p('scope'),tail:cell})},reader=>
     direct('inputDispatch',{host:p('host'),scope:p('scope'),cell,reader,owner:p('owner'),wrongBoundary:p('wrongBoundary')}))))
  },
  fold:owner=>direct('ownedTransport',{host:p('host'),transport:p('transport'),scope:p('scope'),owner,wrongBoundary:p('wrongBoundary')})
 }})
}
