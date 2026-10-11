import assert from 'node:assert/strict'
const string={kind:'primitive',primitive:'string'},unit={kind:'primitive',primitive:'unit'}
const u={kind:'unit'},sv=value=>({kind:'string',value}),iv=n=>({kind:'int64',value:String(n)})
const returned=(value,delay=0)=>({kind:'return',value,...(delay?{delayMillis:String(delay)}:{})})
const success={kind:'result',okType:unit,errorType:string,isOk:true,value:u}
const failure={kind:'result',okType:unit,errorType:string,isOk:false,value:sv('worker failed')}
function host(count,mode) {
  const cases=[]
  const add=(id,phase,response)=>cases.push({arguments:[{parameter:'id',value:sv(id)},{parameter:'phase',value:sv(phase)}],responses:[response]})
  for(let n=1;n<=count;n++)for(const phase of ['join','p1','p2','final']) {
    let response=returned(success,phase==='join'?n:phase==='p2'?5:1)
    if(phase==='p2'&&((mode==='cancel-one'&&n===2)||mode==='parent-exit'))response={kind:'awaitCancellation',value:success}
    if(phase==='p2'&&n===2&&mode==='worker-error')response=returned(failure,1)
    if(phase==='p2'&&n===2&&mode==='host-exception')response={kind:'throw',message:'worker host failed'}
    add(`w${n}`,phase,response)
  }
  for(const phase of ['cancel','stop','exit'])add('control',phase,returned(success,4))
  return {kind:'fakeCapability',capability:'Host',operations:[
    {operation:'open',responses:[returned(success)]},{operation:'close',responses:[returned(u)]},
    {operation:'pause',cases},{operation:'observe',responses:Array.from({length:count*4+3},()=>returned(u))},
    {operation:'ready',responses:[returned(u)]},{operation:'save',responses:[returned(u)]},
  ]}
}
export function makeCases() {
  return [
    ...[2,3,4].map(count=>({id:`normal-${count}`,count,mode:'normal'})),
    ...['worker-error','cancel-one','stop-race','parent-exit','host-exception'].map(mode=>({id:mode,count:4,mode})),
  ].map(c=>({...c,function:'run',arguments:[host(c.count,c.mode),iv(c.count),sv(c.mode)]}))
}
const arg=(call,name)=>call.arguments.find(a=>a.parameter===name)?.value?.value
export function checkCase(c,response) {
  const fault=c.mode==='host-exception'
  assert.equal(response.ok,!fault,JSON.stringify(response))
  if(fault)assert.equal(response.error.code,'RunRuntimeFailure')
  const calls=fault?response.error.calls:response.result.calls
  assert.equal(calls.filter(x=>x.operation==='open').length,1,c.id)
  assert.equal(calls.filter(x=>x.operation==='close').length,1,c.id)
  assert.equal(calls.at(-1).operation,'close',c.id)
  const ready=calls.findIndex(x=>x.operation==='ready')
  assert.ok(ready>=0,c.id)
  const joins=calls.map((x,i)=>({x,i})).filter(({x})=>x.operation==='pause'&&arg(x,'phase')==='join')
  assert.equal(joins.length,c.count,c.id)
  assert.ok(joins.every(({i})=>i>ready),'registration producers must join after collector starts')
  assert.equal(calls.filter(x=>x.operation==='save').length,fault||c.mode==='parent-exit'?0:1,c.id)
  if(fault)return {id:c.id,hostFaultPropagated:true,closedExactlyOnce:true}
  const actual=response.result.actual
  assert.equal(actual.kind,'result')
  assert.equal(actual.isOk,c.mode!=='parent-exit',c.id)
  const raw=typeof actual.value==='string'?actual.value:actual.value.value
  const log=c.mode==='parent-exit'?(assert.ok(raw.startsWith('cancelled|')),raw.slice('cancelled|'.length)):raw
  const entries=log.split(';').filter(Boolean)
  const observations=calls.filter(x=>x.operation==='observe'&&arg(x,'kind')!=='stop')
  const accepted=observations.filter(x=>arg(x,'accepted')===true).map(x=>`${arg(x,'id')}:${arg(x,'kind')}:${arg(x,'sequence')}`)
  assert.deepEqual([...entries].sort(),[...accepted].sort(),`${c.id}: all accepted events exactly once`)
  assert.equal(new Set(entries).size,entries.length,`${c.id}: no duplicates`)
  const workers=new Map()
  for(const item of entries) {
    const [id,kind,rawSequence]=item.split(':'),seq=Number(rawSequence),old=workers.get(id)
    if(!old)assert.deepEqual([kind,seq],['register',0],item)
    else {assert.ok(!['final','error'].includes(old.kind),item);assert.equal(seq,old.seq+1,item)}
    workers.set(id,{kind,seq})
  }
  if(c.mode==='normal') {
    assert.equal(entries.length,c.count*4)
    assert.ok([...workers.values()].every(w=>w.kind==='final'&&w.seq===3))
  }
  if(c.mode==='worker-error') {
    assert.deepEqual(workers.get('w2'),{kind:'error',seq:2})
    assert.ok([...workers].filter(([id])=>id!=='w2').every(([,w])=>w.kind==='final'),'error is isolated')
  }
  if(c.mode==='cancel-one') {
    assert.ok(!entries.some(x=>x==='w2:final:3'))
    assert.ok([...workers].filter(([id])=>id!=='w2').every(([,w])=>w.kind==='final'),'cancel is isolated')
  }
  if(c.mode==='stop-race')assert.ok(observations.some(x=>arg(x,'accepted')===false),'post-Stop sends reject')
  return {id:c.id,accepted:accepted.length,rejected:observations.length-accepted.length,closedExactlyOnce:true,perWorkerOrder:true,lossless:true,log}
}
