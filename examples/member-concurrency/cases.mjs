import {request as q} from '../member-lifecycle/cases.mjs'
import {argumentsFor as controllerArgs} from '../member-controller/cases.mjs'
import assert from 'node:assert/strict'
const begin=(op,label,slot,member=0)=>q('begin',label,slot,{mode:op,previous:member})
const join=slot=>q('join',`join${slot}`,slot)
const gate=label=>q('gate',label)
const ready=[q('spawn','a'),q('spawn','b'),gate('snapshot1'),gate('release1'),q('wait','settled-a',1),gate('provider2')]
const end=[q('finish','finish'),q('inspect','retained')]
export const scenarios=()=>[
 {id:'capacity-contention',kind:'capacity',script:[q('spawn','a'),gate('provider1'),begin('spawn','winner',1),gate('spawn-held'),begin('spawn','loser',2),gate('queued:loser'),gate('spawn-release'),join(1),join(2),...end]},
 {id:'cancel-queued-spawn',kind:'queued-cancel',script:[q('spawn','a'),gate('provider1'),begin('spawn','winner',1),gate('spawn-held'),begin('spawn','cancelled',2),gate('queued:cancelled'),q('cancel','cancel2',2),join(2),gate('spawn-release'),join(1),q('spawn','later'),...end]},
 ...[false,true].map(cancel=>({id:`duplicate-integration-${cancel?'cancel':'clear'}`,kind:'duplicate',cancel,script:[...ready,begin('integrate','first',1,1),gate('commit-held'),begin('integrate','second',2,1),gate('queued:second'),...(cancel?[q('cancel','cancel1',1)]:[]),gate('commit-release'),join(1),join(2),q('inspect','ledger',1),...end]})),
 {id:'wait-stop-during-integration',kind:'wait-stop',script:[...ready,begin('integrate','first',1,1),gate('commit-held'),begin('wait','waiting',2,2),begin('stop','stopping',3,2),join(2),join(3),gate('commit-release'),join(1),...end]},
 {id:'close-cutoff-before-drain',kind:'close',script:[...ready,begin('integrate','first',1,1),gate('commit-held'),begin('finish','closing',2),gate('queued:closing'),gate('commit-release'),join(1),gate('close-held'),begin('spawn','late',3),join(3),gate('close-release'),join(2),q('inspect','retained')]},
].map(c=>({...c,patch:'synthetic-patch',providers:{1:['capacity','queued-cancel'].includes(c.kind)?'hold':'complete',2:'hold'}}))

export const arg=(call,key)=>call.arguments.find(a=>a.parameter===key)?.value?.value
export function normalize(c,calls){
 const replies=calls.filter(x=>x.operation==='reply'&&!arg(x,'label').startsWith('@')).map(x=>({label:arg(x,'label'),record:arg(x,'record')}))
 let request='worker'
 // Real effects carry AsyncLocal labels; virtual step attribution follows the
 // exclusive entry marker. Independent observations do not mutate that marker.
 const events=[]
 for(const x of calls){
  if(x.operation==='gate'&&arg(x,'label').startsWith('enter:'))request=arg(x,'label').slice(6)
  if(x.operation==='step')events.push({request:x.request??request,operation:arg(x,'operation'),patch:arg(x,'patch')})
  if(x.operation==='workspaceCreate')events.push({request:x.request??request,operation:'workspaceCreate',patch:arg(x,'workspace')})
  if(x.operation==='reply')events.push({request:arg(x,'label'),operation:'reply',patch:arg(x,'record')})
  if(x.operation==='gate')events.push({request:x.request??'coordinator',operation:'gate',patch:arg(x,'label')})
  if(x.operation==='close')events.push({request:'owner',operation:'release',patch:''})
 }
 return {id:c.id,replies,state:replies.find(x=>x.label==='retained')?.record,events,released:calls.filter(x=>x.operation==='close').length}
}
export function projection(row){
 const relevant=new Set(['workspaceCreate','clean-source','clean-member','validate','ancestor','paths-member','paths-leader','close-agent','patch','check','apply','commit','head','save','reverse-check','reverse-apply'])
 const groups={}
 for(const e of row.events)if(relevant.has(e.operation))(groups[e.request]??=[]).push({operation:e.operation,patch:e.patch})
 return {id:row.id,replies:[...row.replies].sort((a,b)=>a.label.localeCompare(b.label)),state:row.state,effects:groups,released:row.released}
}
export function validate(c,row){
 const replies=Object.fromEntries(row.replies.map(r=>[r.label,r.record]))
 assert.equal(row.released,1)
 assert.equal(replies.a,'spawn:1')
 if(c.kind==='capacity'||c.kind==='queued-cancel'){
  assert.equal(replies.winner,'spawn:2')
  assert.equal(replies[c.kind==='capacity'?'loser':'later'],'reject:capacity')
  if(c.kind==='queued-cancel')assert.equal(replies.cancelled,'err:cancelled')
  assert.equal(row.events.filter(x=>x.operation==='workspaceCreate').length,2)
 }else{
  assert.equal(replies.first,c.cancel?'err:cancelled':'ok:integrated-head')
  for(const phase of ['check','apply','commit','head','save'])assert.equal(row.events.filter(e=>e.operation===phase).length,1,phase)
  const save=row.events.findIndex(e=>e.operation==='save')
  const first=row.events.findIndex(e=>e.operation==='reply'&&e.request==='first')
  assert.ok(save>=0&&first>save,'protected tail finishes before first caller settlement')
 }
 if(c.kind==='duplicate'){
  assert.equal(replies.second,'ok:integrated-head')
  assert.match(replies.ledger,/integrated-head;/)
  const transferred=row.events.findIndex(e=>e.operation==='gate'&&e.patch==='released:first')
  const entered=row.events.findIndex(e=>e.operation==='gate'&&e.patch==='enter:second')
  if(transferred>=0)assert.ok(transferred>row.events.findIndex(e=>e.operation==='save')&&entered>transferred,'HEAD/save precede ownership transfer and duplicate entry')
 }
 const index=(operation,patch)=>row.events.findIndex(e=>e.operation===operation&&e.patch===patch)
 if(c.kind==='wait-stop')for(const label of ['waiting','stopping']){
  assert.match(replies[label],/^2,stopped,/)
  assert.ok(row.events.findIndex(e=>e.operation==='reply'&&e.request===label)<index('gate','commit-release'),'wait/stop settle while integration held')
 }
 if(c.kind==='queued-cancel')assert.ok(index('reply','err:cancelled')<index('gate','spawn-release'),'queued caller settles without owner release')
 if(c.kind==='close'){
  assert.equal(replies.late,'reject:closing')
  assert.ok(index('gate','close-held')>row.events.findIndex(e=>e.operation==='save'),'close queues behind protected integration')
  assert.ok(index('reply','reject:closing')<index('gate','close-release'),'cutoff rejects before drain finishes')
 }
 assert.ok(row.events.every((e,i)=>!['workspaceCreate','snapshot','checks','memberClose','save','commit','head'].includes(e.operation)||i<index('release','')),'worker and mutation effects precede the one release')
 return row
}

export function argumentsFor(c){
 const base={...c,cancel:undefined,id:'overlap-continuation',member:['a','b'],leader:['c'],requests:1,expected:'integrated-head'}
 const args=controllerArgs(base),host=args[0],op=name=>host.operations.find(x=>x.operation===name)
 // Timing is a virtual schedule, not a substitute for real/Python barriers.
 op('reply').responses.forEach(x=>delete x.delayMillis)
 op('workspaceCreate').responses[1].delayMillis='100'
 if(!['capacity','queued-cancel'].includes(c.kind))delete op('workspaceCreate').responses[1].delayMillis
 op('snapshot').cases.forEach(x=>x.responses.forEach(r=>{r.delayMillis='20'}))
 op('gate').cases=[]
 const labels=new Set([...c.script.filter(x=>x.op==='gate').map(x=>x.label),...c.script.map(x=>`enter:${x.label}`),...c.script.map(x=>`request-start:${x.label}`),...c.script.map(x=>`released:${x.label}`),'worker-entry:1','worker-entry:2','stop-join:1','stop-join:2','close-held-worker'])
 const delays={'spawn-held':1,'spawn-release':110,'snapshot1':130,'release1':25,'provider1':110,'provider2':110,'commit-held':101,'commit-release':300,'close-held':1,'close-held-worker':100,'close-release':110}
 for(const label of labels)op('gate').cases.push({arguments:[{parameter:'label',value:{kind:'string',value:label}}],responses:Array.from({length:2},()=>({kind:'return',value:{kind:'unit'},...(delays[label]?{delayMillis:String(delays[label])}:{})}))})
 op('step').cases.forEach(x=>x.responses.forEach(r=>{delete r.delayMillis;if(x.arguments[0].value.value==='commit')r.delayMillis='250'}))
 return args
}
