// Ordinary graph authoring. The host supplies Git effects, never integration policy.
import {string,bool,unit,list,result,named,cap,p,text,pat,bind,field,construct,ok,err,direct,cond,eqStr,invoke,matchOption} from '../dynamic-worker-collector/lib.mjs'
const E=named('Candidate'), O=named('Outcome'), R=result(string), paths=list(string)
const fields=o=>Object.entries(o).map(([displayName,type])=>({displayName,type}))
const f=k=>field(p('candidate'),'Candidate',k)
const mr=(input,okBody,errBody)=>({kind:'matchResult',input,okBody,errBody})
const io=(operation,patch=text(''))=>invoke(p('host'),'Git','step',{operation:text(operation),patch})
const call=(fn,extra={})=>direct(fn,{host:p('host'),candidate:p('candidate'),...extra})
const failure=()=>err(pat('error'),string)
const step=(operation,next,patch)=>mr(io(operation,patch),next,failure())
const first=source=>matchOption({kind:'listItemAt',list:source,index:{kind:'int64Literal',value:'0'}},pat(),text(''))
const set=source=>({kind:'setFromList',value:source})
export async function author(s){
 await s.call('open',{mode:'create',snapshot:s.snapshot})
 await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[
  {displayName:'Candidate',typeKind:{kind:'product',fields:fields({settled:bool,latest:bool,discarded:bool,status:string,commit:string,integrated:string})}},
  {displayName:'Outcome',typeKind:{kind:'product',fields:fields({candidate:E,result:R})}}
 ]}}))
 await s.publish(await s.call('request',{op:'simulateCreateEffect',arguments:{displayName:'Io'}}))
 await s.publish(await s.call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'Git',operations:[{displayName:'step',parameters:fields({operation:string,patch:string}),resultType:result(paths),effect:'Io'}]}}))
 const params={host:cap('Git'),candidate:E}
 await s.create('record',params,R,step('head',bind(first(pat()),step('save',ok(pat('value',1)),pat()))))
 await s.create('rollback',{...params,patch:string,cause:string},R,
  step('reverse-check',step('reverse-apply',err(p('cause'),string),p('patch')),p('patch')))
 await s.create('commit',{...params,patch:string},R,
  mr(io('commit'),call('record'),call('rollback',{patch:p('patch'),cause:pat('error')})))
 await s.create('apply',{...params,patch:string},R,
  step('check',step('apply',call('commit',{patch:p('patch')}),p('patch')),p('patch')))
 await s.create('patch',params,R,step('close-agent',step('patch',
  bind(first(pat()),cond(eqStr(pat(),text('')),call('record'),call('apply',{patch:pat()}))))))
 await s.create('overlap',{...params,memberPaths:paths},R,
  step('paths-leader',cond({kind:'int64GreaterThan',left:{kind:'setSize',value:{kind:'setIntersection',left:set(p('memberPaths')),right:set(pat())}},right:{kind:'int64Literal',value:'0'}},err(text('overlap'),string),call('patch'))))
 await s.create('validate',params,R,
  step('clean-source',step('clean-member',step('validate',step('ancestor',step('paths-member',call('overlap',{memberPaths:pat()})))))))
 await s.create('eligible',params,R,
  cond(eqStr(f('integrated'),text('')),
   cond(eqStr(f('status'),text('completed')),cond(eqStr(f('commit'),text('')),err(text('unsnapshotted'),string),call('validate')),err(text('status'),string)),ok(f('integrated'))))
 await s.create('integrate',params,R,
  cond(f('latest'),cond(f('discarded'),err(text('discarded'),string),cond(f('settled'),call('eligible'),err(text('running'),string))),err(text('superseded'),string)))
 const updated=value=>({kind:'productUpdate',source:p('candidate'),type:'Candidate',fields:[{field:'integrated',value}]})
 await s.create('attempt',params,O,mr(call('integrate'),construct('Outcome',{candidate:updated(pat()),result:ok(pat())}),construct('Outcome',{candidate:p('candidate'),result:failure()})))
 await s.create('attemptFold',{host:cap('Git'),acc:O,item:unit},O,
  direct('attempt',{host:p('host'),candidate:field(p('acc'),'Outcome','candidate')}))
 await s.create('run',{...params,requests:list(unit)},O,
  {kind:'listEffectFold',source:p('requests'),seed:construct('Outcome',{candidate:p('candidate'),result:ok(f('integrated'))}),function:'attemptFold',context:[{parameter:'host',value:p('host')}]})
 const d=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:d.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:[]}}})
 return {artifact,nodes:s.nodes}
}
