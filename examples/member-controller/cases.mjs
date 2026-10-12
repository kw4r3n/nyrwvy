import assert from 'node:assert/strict'
import {request as q,argumentsFor as lifecycleArgs} from '../member-lifecycle/cases.mjs'
import {responseFor,payloadFor} from '../member-integration/cases.mjs'
import {sv,returned} from '../dynamic-worker-collector/lib.mjs'
const ready=[q('spawn','a'),q('spawn','b'),q('gate','snapshot1'),q('integrate','early',1),q('observe','observer'),q('gate','release1',1,{mode:'settle'}),q('wait','settled',1)]
const end=[q('stop','peer-stop',2),q('finish','finish'),q('spawn','closed'),q('inspect','retained')]
const attempt=(label='integration',mode='all')=>q('integrate',label,1,{mode})
const rawScript=mode=>[...ready,attempt('integration',mode),q('inspect','peer-alive',2),attempt('retry'),q('wait','still-settled',1),...end]
export const scenarios=()=>[
 ...['synthetic-patch',''].flatMap(patch=>[
  {id:`ledger-${patch?'patch':'empty'}`,patch,fail:'save',script:[...ready,attempt(),attempt('retry'),attempt('third'),q('spawn','integrated-previous',0,{previous:1}),...end]},
  ...['apply','commit','head','save'].filter(phase=>patch||['head','save'].includes(phase)).map(cancel=>({id:`cancel-${patch?'patch':'empty'}-${cancel}`,patch,cancel,script:[...ready,attempt('integration','cancel'),attempt('retry'),...end]})),
 ]),
 ...['commit','head','save'].map(fail=>({id:`retry-${fail}`,fail,script:[...ready,attempt(),attempt('retry'),...end]})),
 ...['commit','reverse-check','reverse-apply'].map(cancel=>({id:`rollback-cancel-${cancel}`,fail:'commit',cancel,script:[...ready,attempt('integration','cancel'),attempt('retry'),...end]})),
 ...['reverse-check','reverse-apply'].map(rollbackFail=>({id:`rollback-error-${rollbackFail}`,fail:'commit',rollbackFail,cancel:rollbackFail,script:[...ready,attempt('integration','cancel'),...end]})),
 {id:'save-error-cancel',fail:'save',cancel:'save',script:[...ready,attempt('integration','cancel'),attempt('retry'),...end]},
 {id:'head-error-cancel',fail:'head',cancel:'head',script:[...ready,attempt('integration','cancel'),attempt('retry'),...end]},
 {id:'lineage',script:[...ready,q('spawn','continuation',0,{previous:1}),q('gate','snapshot3'),q('gate','release3',3,{mode:'settle'}),q('wait','new-settled',3),attempt('old'),q('integrate','new',3),q('discard','discard',3),q('discard','twice',3),q('integrate','discarded',3),q('spawn','discarded-previous',0,{previous:3}),...end]},
 {id:'checks-boundary',checksGate:true,script:[q('spawn','a'),q('spawn','b'),q('gate','snapshot1'),attempt('snapshot-running'),q('gate','release1'),q('gate','checks1'),attempt('checks-running'),q('spawn','full'),q('gate','release-checks',1,{mode:'settle'}),q('wait','settled',1),attempt(),...end]},
 {id:'finish-cancel-snapshot',finishCancel:true,script:[q('spawn','a'),q('spawn','b'),q('gate','snapshot1'),q('finish','finish',0,{mode:'cancel'}),q('spawn','closed'),q('inspect','retained'),attempt('after-finish'),attempt('after-finish-again')]},
 {id:'expected-worker-error',providerError:true,script:[q('spawn','a'),q('spawn','b'),q('gate','provider1',1,{mode:'settle'}),q('wait','settled',1),attempt(),...end]},
 {id:'raw-host-fault',rawFault:true,script:rawScript('all')},
 ...['commit','reverse-check','reverse-apply'].map(cancel=>({id:`raw-cancel-${cancel}`,rawFault:true,cancel,script:rawScript('cancel')})),
 ...['reverse-check','reverse-apply'].flatMap(rollbackFail=>[false,true].flatMap(rawRollbackFault=>[false,true].map(pending=>({id:`raw-rollback-${rawRollbackFault?'raw':'typed'}-${rollbackFail}-${pending?'cancel':'clear'}`,rawFault:true,rollbackFail,rawRollbackFault,...(pending?{cancel:rollbackFail}:{}),script:rawScript(pending?'cancel':'all')})))),
].map(c=>({...c,patch:c.patch??'synthetic-patch',member:['a','b'],leader:['c'],providers:{1:c.providerError?'inner-timeout':'complete',2:'hold',3:'complete'},requests:1,expected:'integrated-head',expectedReplies:{early:'err:running',observer:'err:cancelled',closed:'reject:closing',...(c.expectedReplies??{})}}))
const operations=['clean-source','clean-member','validate','ancestor','paths-member','paths-leader','close-agent','patch','check','apply','commit','head','save','reverse-check','reverse-apply']
export function argumentsFor(c){
 const args=lifecycleArgs({...c,id:c.providerError?'inner-timeout':'overlap-continuation'})
 const host=args[0],op=k=>host.operations.find(x=>x.operation===k)
 // All completed providers reach held snapshot gates before spawn's audit.
 op('snapshot').cases.forEach(x=>{x.responses[0].delayMillis='100'})
 op('reply').responses.forEach(x=>{x.delayMillis='2'})
 if(c.checksGate)op('checks').cases[0].responses[0].delayMillis='100'
 const gs=op('gate').cases
 for(const x of gs){if(x.arguments[0].value.value.startsWith('worker-entry:'))x.responses[0].delayMillis='100';if(x.arguments[0].value.value.startsWith('snapshot'))x.responses[0].delayMillis='102'}
 gs.push({arguments:[{parameter:'label',value:sv('observation-await')}],responses:[returned({kind:'unit'},1)]})
 const phases=[...operations.slice(0,8),...(c.patch?['check','apply','commit']:[]),'head','save'];const cancelIndex=phases.indexOf(c.cancel)
 gs.push({arguments:[{parameter:'label',value:sv('integration-await')}],responses:[returned({kind:'unit'},Math.max(1,cancelIndex*10+1))]})
 gs.push({arguments:[{parameter:'label',value:sv('integration-release')}],responses:[returned({kind:'unit'})]})
 gs.push({arguments:[{parameter:'label',value:sv('finish-await')}],responses:[returned({kind:'unit'},1)]})
 gs.push({arguments:[{parameter:'label',value:sv('finish-release')}],responses:[returned({kind:'unit'})]})
 if(c.finishCancel){gs.find(x=>x.arguments[0].value.value==='stop-join:1').responses.forEach(x=>{x.delayMillis='110'})}
 if(c.checksGate){gs.find(x=>x.arguments[0].value.value==='release1').responses[0].delayMillis='110';gs.find(x=>x.arguments[0].value.value==='release-checks').responses[0].delayMillis='110'}
 // cancelled integration must occur at the selected rollback phase, not list index order
 if(c.cancel?.startsWith('reverse-'))gs.find(x=>x.arguments[0].value.value==='integration-await').responses[0].delayMillis=String((11+(c.cancel==='reverse-apply'?1:0))*10+1)
 host.operations.push({operation:'step',cases:operations.map(operation=>({arguments:[{parameter:'operation',value:sv(operation)},{parameter:'patch',value:sv(payloadFor(c,operation))}],responses:Array.from({length:8},(_,occurrence)=>{
  if(c.rawFault&&operation==='commit')return {kind:'throw',message:'raw commit fault',delayMillis:'10'}
  if(c.rawRollbackFault&&operation===c.rollbackFail)return {kind:'throw',message:operation,delayMillis:'10'}
  const r=responseFor(c,operation,occurrence)
  return returned({kind:'result',okType:{kind:'builtinGeneric',generic:'list',arguments:[{kind:'primitive',primitive:'string'}]},errorType:{kind:'primitive',primitive:'string'},isOk:r.ok,value:r.ok?{kind:'list',itemKind:'string',items:r.value}:sv(r.value)},10)
 })}))})
 return args
}
export const arg=(x,k)=>x.arguments.find(a=>a.parameter===k)?.value?.value
export function normalize(c,calls){
 const replies=calls.filter(x=>x.operation==='reply').map(x=>({label:arg(x,'label'),record:arg(x,'record')}))
 const observations=[];const steps=[]
 for(const call of calls){if(call.operation==='step')steps.push({operation:arg(call,'operation'),patch:arg(call,'patch')});if(call.operation==='reply'&&arg(call,'label').endsWith(':state'))observations.push({label:arg(call,'label').slice(0,-6),state:arg(call,'record'),steps:structuredClone(steps)})}
 return {id:c.id,replies,observations,steps}
}
export function validate(c,response){
 assert.equal(response.ok,true,JSON.stringify(response).slice(0,1500))
 const calls=response.result.calls
 assert.equal(calls.filter(x=>x.operation==='open').length,1)
 assert.equal(calls.filter(x=>x.operation==='close').length,1)
 const closed=calls.findIndex(x=>x.operation==='close')
 assert.ok(calls.every((x,i)=>!['provider','snapshot','checks','memberClose'].includes(x.operation)||i<closed),'drain before provider release')
 const row=normalize(c,calls),replies=Object.fromEntries(row.replies.map(x=>[x.label,x.record]))
 for(const [label,expected] of Object.entries(c.expectedReplies))if(c.script.some(r=>r.label===label))assert.equal(replies[label],expected,`${c.id}:${label}`)
 if(c.rawFault){
  const cause=c.rollbackFail??'raw commit fault'
  assert.equal(replies.integration,`err:${cause}`,'raw/rollback fault wins pending cancellation')
  assert.equal(replies.retry,`err:${cause}`,'subsequent serialized integration remains available')
  assert.match(replies['peer-alive'],/^2,running,/,'raw failure leaves peer alive')
  const attempts=row.steps.filter(x=>x.operation==='commit').length
  assert.equal(attempts,2)
  assert.equal(row.steps.filter(x=>x.operation==='reverse-check').length,attempts,'one rollback per failed commit')
  assert.equal(row.steps.filter(x=>x.operation==='reverse-apply').length,c.rollbackFail==='reverse-check'?0:attempts)
  assert.equal(row.steps.filter(x=>['head','save'].includes(x.operation)).length,0,'no ledger after failed commit')
 }
 assert.equal(row.observations.length,c.script.length,'one audit per serialized request')
 assert.equal(replies['integration:pending'],c.cancel?'pending':undefined)
 if(c.finishCancel)assert.equal(replies['finish:pending'],'pending')
 if(c.fail==='save'||(c.cancel&&['apply','commit','head','save'].includes(c.cancel)&&!c.fail&&!c.rawFault)){
  assert.equal(row.steps.filter(x=>x.operation==='save').length,1,'ledger suppresses duplicate save')
  assert.equal(replies.retry,`ok:${c.patch?'integrated-head':'source-head'}`)
 }
 for(const x of row.steps)assert.equal(x.patch,payloadFor(c,x.operation),'exact patch/save payload')
 assert.equal(replies['finish:outcome'],c.finishCancel?'err:cancelled':'ok:drained')
 assert.ok(calls.findIndex(x=>x.operation==='reply'&&arg(x,'label')==='finish')>closed,'finish acknowledgement follows provider release')
 for(const id of [1,2,3]){
  const positions=op=>calls.flatMap((x,i)=>x.operation===op&&arg(x,'id')===String(id)?[i]:[])
  const provider=positions('provider'),close=positions('memberClose'),snapshot=positions('snapshot'),checks=positions('checks')
  assert.ok([provider,close,snapshot,checks].every(x=>x.length<=1),'one worker side effect per attempt')
  if(snapshot.length)assert.ok(close.length===1&&close[0]<snapshot[0],'member cleanup precedes snapshot')
  if(checks.length)assert.ok(snapshot.length===1&&snapshot[0]<checks[0],'snapshot precedes checks')
 }
 assert.equal(calls.filter(x=>x.operation==='workspaceDiscard').length,c.id==='lineage'?1:0,'explicit discard once; retained otherwise')
 return row
}
