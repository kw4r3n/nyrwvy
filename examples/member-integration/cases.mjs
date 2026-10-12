import assert from 'node:assert/strict'
import {sv,returned} from '../dynamic-worker-collector/lib.mjs'
const pre=['clean-source','clean-member','validate','ancestor','paths-member','paths-leader','close-agent','patch']
const full=[...pre,'check','apply','commit','head','save']
const candidate={settled:true,latest:true,discarded:false,status:'completed',commit:'member-commit',integrated:''}
export const scenarios=()=>[
 {id:'disjoint',trace:full},
 {id:'repeat-idempotent',requests:2,trace:full},
 {id:'already-integrated',candidate:{integrated:'prior-head'},trace:[],expected:'prior-head'},
 {id:'superseded-integrated',candidate:{latest:false,integrated:'prior-head'},trace:[],error:'superseded'},
 {id:'discarded',candidate:{discarded:true},trace:[],error:'discarded'},
 {id:'running',candidate:{settled:false,status:'completed'},trace:[],error:'running'},
 {id:'failed',candidate:{status:'failed'},trace:[],error:'status'},
 {id:'stopped',candidate:{status:'stopped'},trace:[],error:'status'},
 {id:'no-snapshot',candidate:{commit:''},trace:[],error:'unsnapshotted'},
 {id:'overlap',leader:['b','a'],trace:pre.slice(0,6),error:'overlap'},
 {id:'duplicate-disjoint-paths',member:['a','a','b'],leader:['c','c'],trace:full},
 {id:'empty-paths',member:[],leader:[],trace:full},
 {id:'empty-patch',patch:'',trace:[...pre,'head','save'],expected:'source-head'},
 ...['clean-source','clean-member','validate','ancestor','paths-member','paths-leader','close-agent','patch','check','apply','head'].map(fail=>({id:`fail-${fail}`,fail,trace:full.slice(0,full.indexOf(fail)+1),error:fail})),
 {id:'commit-rollback',fail:'commit',trace:[...pre,'check','apply','commit','reverse-check','reverse-apply'],error:'commit'},
 {id:'rollback-check-fails',fail:'commit',rollbackFail:'reverse-check',trace:[...pre,'check','apply','commit','reverse-check'],error:'reverse-check'},
 {id:'rollback-apply-fails',fail:'commit',rollbackFail:'reverse-apply',trace:[...pre,'check','apply','commit','reverse-check','reverse-apply'],error:'reverse-apply'},
 {id:'retry-after-rollback',fail:'commit',requests:2,trace:[...pre,'check','apply','commit','reverse-check','reverse-apply',...full]},
].map(c=>({...c,candidate:{...candidate,...c.candidate},member:c.member??['a','b'],leader:c.leader??['c'],patch:c.patch??'synthetic-patch',requests:c.requests??1,expected:c.expected??'integrated-head'}))
export function responseFor(c,operation,occurrence=0){
 if((operation===c.fail&&occurrence===0)||operation===c.rollbackFail)return {ok:false,value:operation}
 return {ok:true,value:operation==='paths-member'?c.member:operation==='paths-leader'?c.leader:operation==='patch'?[c.patch]:operation==='head'?[c.patch?'integrated-head':'source-head']:[]}
}
export const payloadFor=(c,operation)=>['check','apply','reverse-check','reverse-apply'].includes(operation)?c.patch:operation==='save'?(c.patch?'integrated-head':'source-head'):''
export function argumentsFor(c,graph){
 const t=graph.types.find(t=>t.displayName==='Candidate')
 const listValue=items=>({kind:'list',itemKind:'string',items})
 const fake=operation=>{const outcomes=[responseFor(c,operation),responseFor(c,operation,1)];return outcomes.map(r=>returned({kind:'result',okType:{kind:'builtinGeneric',generic:'list',arguments:[{kind:'primitive',primitive:'string'}]},errorType:{kind:'primitive',primitive:'string'},isOk:r.ok,value:r.ok?listValue(r.value):sv(r.value)}))}
 const ops=[...new Set([...c.trace,...full,'reverse-check','reverse-apply'])]
 return [
  {kind:'fakeCapability',capability:'Git',operations:[{operation:'step',cases:ops.map(operation=>({arguments:[{parameter:'operation',value:sv(operation)},{parameter:'patch',value:sv(payloadFor(c,operation))}],responses:fake(operation)}))}]},
  {kind:'product',type:'Candidate',fields:t.fields.map(f=>({field:f.displayName,value:typeof c.candidate[f.displayName]==='boolean'?{kind:'bool',value:c.candidate[f.displayName]}:sv(c.candidate[f.displayName])}))},
  {kind:'list',itemType:{kind:'primitive',primitive:'unit'},items:Array.from({length:c.requests},()=>({kind:'unit'}))}
 ]
}
export function validate(c,response){
 assert.notEqual(response.ok,false,JSON.stringify(response).slice(0,1500))
 const body=response.result??response
 const calls=body.calls??[]
 const arg=(x,name)=>x.arguments.find(a=>a.parameter===name).value.value
 const trace=calls.map(x=>arg(x,'operation'))
 assert.deepEqual(trace,c.trace,c.id+' ordered effects')
 for(const x of calls)assert.equal(arg(x,'patch'),payloadFor(c,arg(x,'operation')))
 const output=body.actual
 const values=output.fields.map(f=>f.value),entry=values.find(x=>x.kind==='product'),outcome=values.find(x=>x.kind==='result')
 const saved=Object.fromEntries(entry.fields.map(f=>[f.field,f.value.value])),integrated=saved.integrated
 assert.deepEqual(saved,{...c.candidate,integrated:c.error?c.candidate.integrated:c.expected},c.id+' complete candidate')
 assert.equal(outcome.isOk,!c.error,c.id)
 assert.equal(outcome.value,c.error??c.expected,c.id)
 assert.equal(integrated,c.error?c.candidate.integrated:c.expected,c.id+' ledger')
 return {id:c.id,trace,result:{ok:outcome.isOk,value:outcome.value},integrated}
}
