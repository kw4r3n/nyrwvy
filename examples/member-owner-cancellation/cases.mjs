import assert from 'node:assert/strict'
import {request as q} from '../member-lifecycle/cases.mjs'
import {argumentsFor as admissionArgs,normalize as admissionNormalize,projection as admissionProjection,arg} from '../member-concurrency/cases.mjs'
export {arg}
const begin=(op,label,slot,member=0)=>q('begin',label,slot,{mode:op,previous:member}),join=slot=>q('join',`join${slot}`,slot),gate=label=>q('gate',label)
const ready=[q('spawn','a'),q('spawn','b'),gate('snapshot1'),gate('release1'),q('wait','settled-a',1),gate('provider2')]
const parked=gate('owner-park'),retained=q('inspect','retained')
export const scenarios=()=>[
 {id:'parent-queued-admission',kind:'queued',script:[q('spawn','a'),gate('provider1'),begin('spawn','winner',1),gate('spawn-held'),begin('spawn','loser',2),gate('queued:loser'),parked,join(1),join(2),begin('spawn','late',3),join(3),q('stop','settled-a',1),retained]},
 ...[false,true].map(rawFault=>({id:`parent-protected-integration-${rawFault?'fault':'clear'}`,kind:'integration',rawFault,script:[...ready,begin('integrate','first',1,1),gate('commit-held'),begin('integrate','second',2,1),gate('queued:second'),parked,join(1),join(2),begin('spawn','late',3),join(3),retained]})),
 {id:'parent-held-member-settlement',kind:'settlement',script:[q('spawn','a'),q('spawn','b'),gate('snapshot1'),parked,q('stop','settled-b',2),retained]},
 ...[false,true].map(releaseFault=>({id:`parent-shutdown-${releaseFault?'fault':'clear'}`,kind:'shutdown',releaseFault,script:[...ready,begin('finish','closing',1),gate('close-held'),parked,begin('spawn','late',3),join(3),join(1),retained]})),
].map(c=>({...c,patch:'synthetic-patch',providers:{1:c.kind==='queued'?'hold':'complete',2:'hold'}}))
export function argumentsFor(c){
 const args=admissionArgs({...c,kind:c.kind==='queued'?'capacity':c.kind==='shutdown'?'close':'duplicate'}),host=args[0],op=n=>host.operations.find(x=>x.operation===n)
 const add=(label,delay=0,awaitCancellation=false)=>{op('gate').cases=op('gate').cases.filter(x=>x.arguments[0].value.value!==label);op('gate').cases.push({arguments:[{parameter:'label',value:{kind:'string',value:label}}],responses:Array.from({length:4},()=>awaitCancellation?{kind:'awaitCancellation',value:{kind:'unit'}}:{kind:'return',value:{kind:'unit'},...(delay?{delayMillis:String(delay)}:{})})})}
 add('owner-park',0,true);add('probe-ready',c.kind==='queued'?160:c.kind==='settlement'?150:c.kind==='shutdown'?350:310);add('probe-cancelled',1);add('probe-release',150)
 if(c.kind==='settlement')op('snapshot').cases[0].responses=[{kind:'throw',message:'snapshot cancelled',delayMillis:'500'}]
 if(c.rawFault)for(const item of op('step').cases){const phase=item.arguments[0].value.value;if(phase==='commit')item.responses[0]={kind:'throw',message:'raw commit fault',delayMillis:'500'}}
 if(c.kind==='integration'&&!c.rawFault)for(const item of op('step').cases)if(item.arguments[0].value.value==='commit')item.responses[0].delayMillis='500'
 if(c.kind==='shutdown')op('gate').cases.find(x=>x.arguments[0].value.value==='close-held-worker').responses.forEach(r=>r.delayMillis='500')
 if(c.kind==='queued')op('workspaceCreate').responses[1].delayMillis='500'
 if(c.releaseFault)op('close').responses=[{kind:'throw',message:'release fault'}]
 return args
}
export function normalize(c,calls,outcome){
 const workers={}
 for(const call of calls)if(['memberClose','snapshot','checks'].includes(call.operation))(workers[arg(call,'id')]??=[]).push(call.operation)
 return {...admissionNormalize(c,calls),outcome,workers}
}
export function projection(row){return {...admissionProjection(row),outcome:row.outcome,workers:row.workers}}
export function validate(c,row){
 assert.equal(row.released,1,'resource release is attempted once')
 const replies=Object.fromEntries(row.replies.map(x=>[x.label,x.record])),at=(op,payload)=>row.events.findIndex(x=>x.operation===op&&x.patch===payload)
 assert.equal(replies.a,'spawn:1')
 assert.ok(row.events.every((e,i)=>!['workspaceCreate','memberClose','snapshot','checks','save','commit','head'].includes(e.operation)||i<at('release','')),'effects drain before release')
 if(c.kind==='queued'){assert.equal(replies.winner,'spawn:2');assert.equal(replies.loser,'err:cancelled');assert.equal(row.events.filter(e=>e.operation==='workspaceCreate').length,2);assert.ok(at('reply','err:cancelled')<at('reply','spawn:2'),'queued caller cancels before in-flight admission settles')}
 if(c.kind==='integration'){
  assert.equal(replies.first,c.rawFault?'err:raw commit fault':'err:cancelled');assert.equal(replies.second,'err:cancelled')
  const tail=c.rawFault?'reverse-apply':'save',settled=row.events.findIndex(x=>x.operation==='reply'&&x.request==='first'),end=at(tail,c.rawFault?c.patch:'integrated-head');assert.ok(end>=0&&end<settled,'protected tail precedes caller reply')
  const second=row.events.findIndex(x=>x.operation==='reply'&&x.request==='second');assert.ok(second>=0&&second<end,'queued cancellation settles while protected mutation is held')
  assert.equal(row.events.filter(e=>e.operation==='commit').length,1)
  if(!c.rawFault)assert.ok(!row.state.includes('integrated-head'),'closed handoff does not retain late ledger')
 }
 if(c.kind!=='settlement')assert.equal(replies.late,'err:cancelled','cancelled queued worker cannot admit')
 assert.equal(row.outcome,c.releaseFault?'error:release fault':c.kind==='settlement'?'error:snapshot cancelled':c.adapted?'err:owner cancelled':'ok','owner cancellation reports only after drain; faults preserve precedence')
 return row
}
