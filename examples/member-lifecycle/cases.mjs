import assert from 'node:assert/strict'
export const request=(op,label,target=0,extra={})=>({op,label,target,previous:0,mode:'all',limit:5000,...extra})
const q=request
export function scenarios() {
 return [
  {id:'overlap-continuation',providers:{1:'complete',2:'hold',3:'complete'},script:[
   q('spawn','a'),q('spawn','b'),q('gate','overlap'),q('gate','snapshot1'),
   q('wait','pending',0,{mode:'any',limit:0}),q('inspect','determined',1),q('spawn','full'),q('spawn','premature',0,{previous:1}),q('discard','live-discard',1),
   q('gate','release1'),q('wait','first-settled',0,{mode:'any',limit:5000}),q('spawn','continuation',0,{previous:1}),q('gate','snapshot3'),q('gate','release3'),
   q('wait','new-settled',3),q('spawn','old-previous',0,{previous:1}),q('discard','old-discard',1),q('stop','stop-b',2),
   q('wait','all-settled'),q('discard','discard-latest',3),q('inspect','related-discarded'),q('discard','twice',3),q('spawn','discarded-previous',0,{previous:3}),q('finish','finish'),q('spawn','closing')],
   expected:{pending:'1,running;2,running;',determined:'1,completed,w1,base,0,false,,;',full:'reject:capacity',premature:'reject:capacity','live-discard':'reject:running',continuation:'spawn:3','old-previous':'reject:superseded','old-discard':'reject:superseded',twice:'reject:discarded','discarded-previous':'reject:discarded',closing:'reject:closing'}},
  {id:'stop-snapshot',providers:{1:'complete'},script:[q('spawn','a'),q('gate','snapshot1'),q('wait','pending',1,{limit:0}),q('spawn','premature',0,{previous:1}),q('stop','stop-snapshot',1),q('wait','settled',1),q('spawn','continue',0,{previous:1}),q('stop','stop-new',2),q('finish','finish')],expected:{pending:'1,running;',premature:'reject:running','stop-snapshot':'1,completed,w1,base,0,false,commit1,passed;',continue:'spawn:2'}},
  {id:'stop-before-start',providers:{1:'hold',2:'hold'},script:[q('spawn','a'),q('stop','stop',1),q('spawn','continue',0,{previous:1}),q('stop','stop-again',2),q('inspect','terminal'),q('finish','finish')],expected:{stop:'1,stopped,w1,base,0,false,,;',continue:'spawn:2'}},
  {id:'stop-during-run',providers:{1:'hold',2:'hold'},script:[q('spawn','a'),q('gate','provider1'),q('wait','timeout',1,{limit:0}),q('stop','stop',1),q('spawn','continue',0,{previous:1}),q('stop','stop-again',2),q('finish','finish')],expected:{timeout:'1,running;',stop:'1,stopped,w1,base,0,false,,;',continue:'spawn:2'}},
  ...['expected-error','inner-timeout','deadline','snapshot-error','checks-failed','checks-unavailable','unexpected-fault'].map(id=>{const continuation=['expected-error','inner-timeout','deadline','snapshot-error'].includes(id);return {id,providers:{1:id==='deadline'?'hold':id,2:'complete',...(continuation?{3:'complete'}:{})},script:[q('spawn','a',0,{limit:id==='deadline'?20:5000}),q('spawn','b',0,{limit:id==='deadline'?20:5000}),q('wait','settled'),...(continuation?[q('spawn','continue-failed',0,{previous:1}),q('wait','continued',3)]:[]),q('finish','finish')],expected:continuation?{'continue-failed':'spawn:3',continued:'3,completed,w1,base,1,false,commit3,passed;'}:{},fault:id==='unexpected-fault'}}),
  {id:'finish-active',providers:{1:'hold',2:'hold'},script:[q('spawn','a'),q('spawn','b'),q('gate','overlap'),q('finish','finish'),q('spawn','closing'),q('inspect','retained')],expected:{closing:'reject:closing',retained:'1,stopped,w1,base,0,false,,;2,stopped,w2,base,0,false,,;'}},
  {id:'finish-snapshot',providers:{1:'complete',2:'hold'},script:[q('spawn','a'),q('spawn','b'),q('gate','snapshot1'),q('finish','finish'),q('spawn','closing'),q('inspect','retained')],expected:{closing:'reject:closing',retained:'1,completed,w1,base,0,false,commit1,;2,stopped,w2,base,0,false,,;'}},
  {id:'checks-gated',providers:{1:'complete',2:'hold'},script:[q('spawn','a'),q('spawn','b'),q('gate','checks1'),q('wait','pending',0,{mode:'any',limit:0}),q('inspect','captured',1),q('spawn','full'),q('spawn','premature',0,{previous:1}),q('stop','stop-checks',1),q('stop','stop-b',2),q('finish','finish')],expected:{pending:'1,running;2,running;',captured:'1,completed,w1,base,0,false,commit1,;',full:'reject:capacity',premature:'reject:capacity','stop-checks':'1,completed,w1,base,0,false,commit1,passed;'}},
  {id:'caller-cancellation-gap',function:'callerCancellationRun',providers:{1:'hold'},script:[q('gate','caller-wait-entered'),q('gate','await-caller-wait')],expected:{'caller-cancellation':'caller:running','member-alive':'member:running'}},
 ]
}
const prim=primitive=>({kind:'primitive',primitive})
const str=prim('string'),i64=prim('int64'),unit=prim('unit')
export const sv=value=>({kind:'string',value}),iv=value=>({kind:'int64',value:String(value)}),u={kind:'unit'}
const ret=(value,delay=0)=>({kind:'return',value,...(delay?{delayMillis:String(delay)}:{})})
const res=(isOk,value,okType=str)=>okType===str?({kind:'result',okKind:'string',errorKind:'string',isOk,value}):({kind:'result',okType,errorType:str,isOk,value:typeof value==='string'?sv(value):value})
const value=(type,fields)=>({kind:'product',type,fields:Object.entries(fields).map(([field,value])=>({field,value}))})
export function argumentsFor(c) {
 const cases=(param,entries)=>Object.entries(entries).map(([key,responses])=>({arguments:[{parameter:param,value:param==='id'?iv(key):sv(key)}],responses}))
 // Virtual barriers are explicit scripted delays. Runtime barriers use the same labels.
 const providers={};for(const [id,kind] of Object.entries(c.providers))providers[id]=[
  kind==='unexpected-fault'?{kind:'throw',message:'unexpected host fault'}:
  kind==='hold'?{kind:'awaitCancellation',value:res(false,'cancelled')}:
  ret(res(!['expected-error','inner-timeout'].includes(kind),kind==='expected-error'?'deadline':kind==='inner-timeout'?kind:'completed'),1)]
 const snaps={};for(const id of Object.keys(c.providers))snaps[id]=[c.id==='snapshot-error'&&id==='1'?ret(res(false,'snapshot failed')):ret(res(true,`commit${id}`),['overlap-continuation','stop-snapshot','finish-snapshot'].includes(c.id)&&['1','3'].includes(id)?100:1)]
 const checks={};for(const id of Object.keys(c.providers))checks[id]=[ret(sv(c.id==='checks-failed'&&id==='1'?'failed':c.id==='checks-unavailable'&&id==='1'?'unavailable':'passed'),c.id==='checks-gated'&&id==='1'?100:0)]
 const gates={};for(const r of c.script.filter(r=>r.op==='gate'))gates[r.label]=[ret(u,r.label.startsWith('release')?110:2)]
 for(const id of [1,2,3])gates[`stop-join:${id}`]=Array.from({length:8},()=>ret(u,['stop-snapshot','finish-snapshot','checks-gated'].includes(c.id)&&id===1?110:0))
 for(const id of [1,2,3])gates[`worker-entry:${id}`]=[c.id==='stop-before-start'?{kind:'awaitCancellation',value:u}:ret(u)]
 const repeated=v=>Array.from({length:80},()=>ret(v))
 const args=[{kind:'fakeCapability',capability:'Host',operations:[
  {operation:'open',responses:[ret(res(true,u,unit))]}, {operation:'close',responses:[ret(u)]},
  {operation:'workspaceCreate',responses:repeated(res(true,u,unit))},{operation:'workspaceDiscard',responses:repeated(res(true,u,unit))},
  // Full argument keys include every parameter; generated below.
  {operation:'provider',cases:cases('id',providers).map(x=>({...x,arguments:[...x.arguments,{parameter:'workspace',value:sv(x.arguments[0].value.value==='3'?'w1':c.id.startsWith('stop-')&&x.arguments[0].value.value==='2'?'w1':`w${x.arguments[0].value.value}`)},{parameter:'previous',value:iv(x.arguments[0].value.value==='3'?1:c.id.startsWith('stop-')&&x.arguments[0].value.value==='2'?1:0)}]}))},
  {operation:'memberClose',responses:repeated(u)},
  {operation:'snapshot',cases:cases('id',snaps).map(x=>({...x,arguments:[...x.arguments,{parameter:'workspace',value:sv(x.arguments[0].value.value==='3'?'w1':`w${x.arguments[0].value.value}`)}]}))},
  {operation:'checks',cases:cases('id',checks).map(x=>({...x,arguments:[...x.arguments,{parameter:'commit',value:sv(`commit${x.arguments[0].value.value}`)}]}))},
  {operation:'gate',cases:cases('label',gates)},{operation:'reply',responses:repeated(u)},
 ]}, {kind:'list',itemType:{kind:'named',name:'Request'},items:c.script.map(r=>value('Request',Object.fromEntries(Object.entries(r).map(([k,v])=>[k,typeof v==='number'?iv(v):sv(v)]))))}]
 return c.function==='callerCancellationRun'?args.slice(0,1):args
}
export function validate(c,response) {
 assert.equal(response.ok,!c.fault,JSON.stringify(response).slice(0,2500))
 const calls=c.fault?response.error.calls:response.result.calls
 assert.equal(calls.filter(x=>x.operation==='open').length,1)
 assert.equal(calls.filter(x=>x.operation==='close').length,1)
 const closeAt=calls.findIndex(x=>x.operation==='close')
 assert.ok(calls.every((x,i)=>!['provider','snapshot','checks','memberClose'].includes(x.operation)||i<closeAt),'close follows all worker effects')
 if(c.fault){assert.equal(response.error.code,'RunRuntimeFailure');return {id:c.id,unexpectedScopeFault:true,providerClosedOnce:true}}
 const arg=(x,key)=>x.arguments.find(a=>a.parameter===key)?.value?.value
 const replies=Object.fromEntries(calls.filter(x=>x.operation==='reply').map(x=>[arg(x,'label'),arg(x,'record')]))
 for(const [label,expected] of Object.entries(c.expected))assert.equal(replies[label],expected,`${c.id}:${label}`)
 if(c.id!=='caller-cancellation-gap')assert.ok(calls.findIndex(x=>x.operation==='reply'&&arg(x,'label')==='finish')>closeAt,'finish acknowledgement follows provider close')
 for(const id of [1,2,3]){
  const positions=op=>calls.flatMap((x,i)=>x.operation===op&&arg(x,'id')===String(id)?[i]:[])
  const provider=positions('provider'),close=positions('memberClose'),snapshot=positions('snapshot'),checks=positions('checks')
  assert.ok(provider.length<=1&&close.length<=1&&snapshot.length<=1&&checks.length<=1,'one effect of each kind per attempt')
  if(snapshot.length)assert.ok(close.length===1&&close[0]<snapshot[0],'provider cleanup precedes snapshot')
  if(checks.length)assert.ok(snapshot.length===1&&snapshot[0]<checks[0],'snapshot precedes checks')
 }
 if(c.id==='stop-before-start')assert.equal(calls.filter(x=>x.operation==='provider').length,0,'stop before provider starts')
 if(['expected-error','inner-timeout','deadline','snapshot-error','checks-failed','checks-unavailable'].includes(c.id)) {
  const status=c.id==='deadline'?'timed_out':['checks-failed','checks-unavailable'].includes(c.id)?'completed':'failed'
  const commit=status==='completed'?'commit1':''
  const check=c.id==='checks-failed'?'failed':c.id==='checks-unavailable'?'unavailable':''
  assert.equal(replies.settled,`1,${status},w1,base,0,false,${commit},${check};2,completed,w2,base,0,false,commit2,passed;`)
 }
 const discards=calls.filter(x=>x.operation==='workspaceDiscard')
 assert.equal(discards.length,c.id==='overlap-continuation'?1:0,'workspaces retained until explicit discard')
 return {id:c.id,replies,providerClosedOnce:true,workspaceDiscards:discards.length,calls:calls.length}
}
