import test from 'node:test'
import assert from 'node:assert/strict'
import {checkCase} from './cases.mjs'
const c={id:'synthetic',leftCount:2,rightCount:2,stopAfter:-1,mode:'normal'}
const av=(parameter,value)=>({parameter,value:{kind:typeof value==='boolean'?'bool':typeof value==='number'?'int64':'string',value:typeof value==='number'?String(value):value}})
const call=(operation,args={})=>({operation,arguments:Object.entries(args).map(([k,v])=>av(k,v))})
function response(){
 const records=[['w1',0],['w2',0],['w1',1],['w2',1]]
 const calls=[call('open'),...records.flatMap(([id,sequence])=>[call('read',{id,sequence}),call('observe',{id,kind:'data',sequence,accepted:true,steps:1}),call('write',{id,sequence,payload:`${id}:${sequence}`})]),...['w1','w2'].map(id=>call('observe',{id,kind:'done',sequence:2,accepted:true,steps:1})),call('finish',{outcome:'done'}),call('close')]
 return {ok:true,result:{actual:{kind:'result',isOk:true,value:'done'},calls}}
}
test('oracle accepts exact normal delivery and linear append counts',()=>{const r=checkCase(c,response());assert.equal(r.accepted,4);assert.equal(r.appendSteps,6)})
test('oracle rejects lost, duplicated and misrouted deliveries',()=>{
 for(const mutate of [r=>r.result.calls.splice(r.result.calls.findIndex(x=>x.operation==='write'),1),r=>r.result.calls.splice(-2,0,r.result.calls.find(x=>x.operation==='write')),r=>{r.result.calls.find(x=>x.operation==='write').arguments.find(a=>a.parameter==='id').value.value='w3'}]){
  const r=response();mutate(r);assert.throws(()=>checkCase(c,r))
 }
})
test('oracle rejects per-worker reordering and cumulative head traversal',()=>{
 const r=response(),writes=r.result.calls.map((x,i)=>({x,i})).filter(({x})=>x.operation==='write');[r.result.calls[writes[0].i],r.result.calls[writes[2].i]]=[r.result.calls[writes[2].i],r.result.calls[writes[0].i]];assert.throws(()=>checkCase(c,r))
 const cost=response();cost.result.calls.find(x=>x.operation==='observe').arguments.find(a=>a.parameter==='steps').value.value='3';assert.throws(()=>checkCase(c,cost))
})
