import assert from 'node:assert/strict'
const unit={kind:'primitive',primitive:'unit'},string={kind:'primitive',primitive:'string'}
const u={kind:'unit'},sv=value=>({kind:'string',value}),iv=n=>({kind:'int64',value:String(n)}),returned=(value,delay=0)=>({kind:'return',value,...(delay?{delayMillis:String(delay)}:{})})
const ok=value=>value.kind==='string'?{kind:'result',okKind:'string',errorKind:'string',isOk:true,value:value.value}:{kind:'result',okType:unit,errorType:string,isOk:true,value}
const failure=(message,okType=unit)=>okType.primitive==='string'?{kind:'result',okKind:'string',errorKind:'string',isOk:false,value:message}:{kind:'result',okType,errorType:string,isOk:false,value:sv(message)}
// Ordinary fake operations permit at most 256 argument cases and 256 total
// scripted responses per operation. Real-runtime workloads remain 256/256.
export function validateFixtureBudget(host){
  for(const op of host.operations){
    assert.ok((op.cases?.length??0)<=256,`${op.operation}: at most 256 argument cases`)
    const count=(op.responses?.length??0)+(op.cases??[]).reduce((n,c)=>n+c.responses.length,0)
    assert.ok(count<=256,`${op.operation}: at most 256 total scripted responses`)
  }
}
export function makeCases(){
  return [
    {id:'normal-small',leftCount:4,rightCount:4},
    {id:'normal-large',leftCount:64,rightCount:64},
    {id:'early-done',leftCount:1,rightCount:128},
    {id:'idle-input',leftCount:4,rightCount:4},
    {id:'graceful-stop',leftCount:4,rightCount:4,stopAfter:1},
    {id:'sink-error',leftCount:4,rightCount:4},
    {id:'source-error',leftCount:4,rightCount:4},
    {id:'sink-exception',leftCount:4,rightCount:4},
    {id:'parent-exit',leftCount:4,rightCount:4,mode:'parent-exit'},
  ].map(c=>{
    c={stopAfter:-1,mode:'normal',...c}
    const reads=[],writes=[]
    for(const [id,count] of [['w1',c.leftCount],['w2',c.rightCount]])for(let n=0;n<count;n++){
      let r=returned(ok(sv(`${id}:${n}`)),c.id==='idle-input'&&id==='w1'&&n===1?25:1)
      if(c.id==='source-error'&&id==='w2'&&n===0)r=returned(failure('source failed',string),2)
      reads.push({arguments:[{parameter:'id',value:sv(id)},{parameter:'sequence',value:iv(n)}],responses:[r]})
      let w=returned(ok(u),10)
      if(c.id==='parent-exit')w={kind:'awaitCancellation',value:ok(u)}
      if(c.id==='sink-error'&&id==='w1'&&n===0)w=returned(failure('sink failed'),10)
      if(c.id==='sink-exception'&&id==='w1'&&n===0)w={kind:'throw',message:'sink exception'}
      writes.push({arguments:[{parameter:'id',value:sv(id)},{parameter:'sequence',value:iv(n)},{parameter:'payload',value:sv(`${id}:${n}`)}],responses:[w]})
    }
    const host={kind:'fakeCapability',capability:'Host',operations:[
      {operation:'open',responses:[returned(ok(u))]}, {operation:'close',responses:[returned(u)]},
      {operation:'read',cases:reads},{operation:'write',cases:writes},
      {operation:'observe',responses:Array.from({length:c.leftCount+c.rightCount+8},()=>returned(u))},
      {operation:'waitAbort',responses:[returned(u,5)]},{operation:'finish',responses:[returned(u)]},
    ]}
    validateFixtureBudget(host)
    return {...c,function:'run',arguments:[host,iv(c.leftCount),iv(c.rightCount),iv(c.stopAfter),sv(c.mode)]}
  })
}
export const arg=(call,name)=>call.arguments?.find(a=>a.parameter===name)?.value?.value
export function checkCase(c,response){
  const fault=c.id==='sink-exception',abort=c.id==='parent-exit',failure=['sink-error','source-error'].includes(c.id)
  assert.equal(response.ok,!fault,JSON.stringify(response).slice(0,3000))
  const calls=fault?response.error.calls:response.result.calls
  if(fault)assert.equal(response.error.code,'RunRuntimeFailure')
  assert.equal(calls.filter(x=>x.operation==='open').length,1)
  assert.equal(calls.filter(x=>x.operation==='close').length,1)
  assert.equal(calls.at(-1).operation,'close')
  assert.equal(calls.filter(x=>x.operation==='finish').length,abort||failure||fault?0:1)
  if(fault)return {id:c.id,hostFault:true,closedOnce:true}
  assert.equal(response.result.actual.kind,'result');assert.equal(response.result.actual.isOk,!abort&&!failure)
  const actual=response.result.actual.value,raw=typeof actual==='string'?actual:actual.value
  assert.equal(raw,abort?'cancelled':c.id==='sink-error'?'sink failed':c.id==='source-error'?'source failed':c.stopAfter>=0?'stopped':'done')
  const observations=calls.filter(x=>x.operation==='observe'),accepted=observations.filter(x=>arg(x,'kind')==='data'&&arg(x,'accepted')===true)
  const writes=calls.filter(x=>x.operation==='write')
  const key=x=>`${arg(x,'id')}:${arg(x,'sequence')}`
  if(!abort&&!failure){
    assert.deepEqual(accepted.map(key).sort(),writes.map(key).sort(),'accepted records delivered exactly once')
    assert.equal(new Set(writes.map(key)).size,writes.length)
    for(const id of ['w1','w2'])assert.deepEqual(writes.filter(x=>arg(x,'id')===id).map(x=>Number(arg(x,'sequence'))),Array.from({length:writes.filter(x=>arg(x,'id')===id).length},(_,i)=>i))
    assert.ok([...accepted,...writes].every(x=>['w1','w2'].includes(arg(x,'id'))),'only the two graph workers appear')
    for(const w of writes)assert.equal(arg(w,'payload'),key(w))
    if(c.stopAfter<0){assert.equal(writes.length,c.leftCount+c.rightCount);assert.equal(writes.filter(x=>arg(x,'id')==='w1').length,c.leftCount);assert.equal(writes.filter(x=>arg(x,'id')==='w2').length,c.rightCount)}
    else assert.ok(writes.length>=c.stopAfter&&writes.length<=c.stopAfter+2,'Stop cutoff drains at most the outstanding records')
  }
  const successful=observations.filter(x=>arg(x,'accepted')===true),steps=successful.reduce((sum,x)=>sum+Number(arg(x,'steps')),0)
  if(!abort&&!failure&&c.stopAfter<0){assert.equal(successful.length,c.leftCount+c.rightCount+2);assert.equal(steps,successful.length,'normal append attempts grow linearly with records')}
  assert.ok(successful.every(x=>Number(arg(x,'steps'))>=1&&Number(arg(x,'steps'))<=2),'no cumulative history walk')
  return {id:c.id,accepted:accepted.length,writeAttempts:writes.length,appendSteps:steps,closedOnce:true,exactDelivery:!abort&&!failure}
}
