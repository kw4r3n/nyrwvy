import assert from 'node:assert/strict'
import {request as q} from '../member-lifecycle/cases.mjs'
import {argumentsFor as inherited} from '../member-determined-settlement/cases.mjs'
import {normalize as normalizeSettlement} from '../member-determined-settlement/cases.mjs'
import {arg} from '../member-owner-cancellation/cases.mjs'
const begin=(op,label,slot)=>q('begin',label,slot,{mode:op})
export const scenarios=()=>[
 {id:'cancel-before-close-publication',order:'admitted',boundary:'snapshot'},
 {id:'cancel-close-during-cleanup',order:'admitted',boundary:'cleanup'},
 {id:'admitted-typed-snapshot',order:'admitted',boundary:'snapshot',snapshotFault:'typed'},
 {id:'admitted-raw-snapshot',order:'admitted',boundary:'snapshot',snapshotFault:'raw'},
 {id:'admitted-release-fault',order:'admitted',boundary:'snapshot',releaseFault:true},
 {id:'close-before-cancel',order:'published',boundary:'snapshot'},
 {id:'checks-before-close',order:'checks',boundary:'checks'},
 {id:'cancel-only-close-caller',order:'caller',boundary:'snapshot',function:'concurrentRun'},
 {id:'cancel-queued-close',order:'queued',boundary:'snapshot',function:'concurrentRun'},
 {id:'admitted-undetermined-provider',order:'admitted',boundary:'provider'},
 {id:'admitted-completed-after-cancel',order:'admitted',boundary:'provider',completedAfterCancel:true},
].map(c=>({...c,kind:'close-intent',patch:'synthetic-patch',providers:{1:c.boundary==='provider'?'hold':'complete'},script:[
 q('spawn','a'),q('gate',c.boundary+'1'),
 ...(c.order==='queued'?[begin('spawn','blocker',2),q('gate','blocker-held')]:[]),
 begin('finish','closing',1),q('gate',c.order==='queued'?'request-started:closing':c.order==='published'?'close-published':'close-admitted'),
 ...(c.function?[q('cancel','cancel-close',1),q('gate','caller-release')]:[q('gate','owner-park')]),
 q('join','joined-close',1),...(c.order==='queued'?[q('join','joined-blocker',2),q('wait','settled-peer',2)]:[]),q('wait','settled',1),q('inspect','retained')
 ]}))
export function argumentsFor(c){
 const args=inherited({...c,boundary:c.boundary==='checks'?'snapshot':c.boundary}),host=args[0],op=name=>host.operations.find(x=>x.operation===name)
 const labels=new Set([...c.script.filter(x=>x.op==='gate').map(x=>x.label),...c.script.map(x=>'request-start:'+x.label),'worker-entry:1','worker-entry:2','stop-join:1','stop-join:2','enter:a','released:a','enter:closing','released:closing','enter:blocker','released:blocker','close-held-worker','probe-ready','probe-cancelled','probe-release'])
 op('gate').cases=[...labels].map(label=>({arguments:[{parameter:'label',value:{kind:'string',value:label}}],responses:Array.from({length:4},()=>label==='owner-park'?{kind:'awaitCancellation',value:{kind:'unit'}}:{kind:'return',value:{kind:'unit'},delayMillis:String(label===c.boundary+'1'?120:label==='enter:closing'&&c.order!=='published'?500:label==='enter:blocker'&&c.order==='queued'?500:label==='close-held-worker'&&c.order==='published'?500:label==='probe-ready'?130:label==='probe-release'||label==='caller-release'?150:0)})}))
 for(const x of op('gate').cases){const label=x.arguments[0].value.value;if(['close-admitted','close-published','blocker-held','request-started:closing'].includes(label))x.responses.forEach(r=>r.delayMillis='50');if(label==='probe-ready')x.responses.forEach(r=>r.delayMillis='300')}
 op('snapshot').cases[0].responses.forEach(x=>x.delayMillis=c.boundary==='snapshot'?'900':'0')
 if(c.boundary==='cleanup')op('memberClose').responses.forEach(x=>x.delayMillis='900')
 op('checks').cases.forEach(x=>x.responses=[{kind:'return',value:{kind:'string',value:'passed'},delayMillis:c.boundary==='checks'?'900':'0'}])
 if(c.function){op('gate').cases.find(x=>x.arguments[0].value.value==='enter:closing').responses.forEach(x=>x.delayMillis=c.order==='caller'?'500':'0')}
 if(c.order==='queued'){
  const copy=name=>{const x=structuredClone(op(name).cases[0]);for(const a of x.arguments){if(a.parameter==='id')a.value.value='2';if(a.parameter==='workspace')a.value.value='w2';if(a.parameter==='commit')a.value.value='commit2'}op(name).cases.push(x);return x}
  copy('provider');copy('snapshot').responses[0].value.value='commit2';copy('checks')
  op('memberClose').responses.push(...structuredClone(op('memberClose').responses))
 }
 return args
}
export function normalize(c,response){
 const row=normalizeSettlement(c,response),calls=response.ok?response.result.calls:response.error.calls
 row.memberEffects={}
 for(const x of calls.filter(x=>['provider','memberClose','snapshot','checks'].includes(x.operation))){const id=arg(x,'id');(row.memberEffects[id]??=[]).push(x.operation)}
 return row
}
export function acceptance(c,row){
 const r=Object.fromEntries(row.replies.map(x=>[x.label,x.record])),count=op=>row.effects.filter(x=>x===op).length
 assert.equal(row.released,1);assert.equal(row.effects.at(-1),'close');assert.equal(count('memberClose'),c.order==='queued'?2:1)
 assert.equal(r.a,'spawn:1');assert.equal(r['@memberClose'],'clear')
 assert.equal(row.outcome,c.snapshotFault==='raw'?'error:raw snapshot fault':c.releaseFault?'error:release fault':c.function?'ok':'err:owner cancelled')
 if(c.order==='queued'){
  assert.equal(r.closing,'err:cancelled');assert.equal(r['@close-publication'],undefined,'requested queued close is not admitted');assert.equal(count('checks'),2)
 }else{
  assert.equal(r['@close-publication'],'accepted','admitted close survives cancelled state handoffs');if(c.snapshotFault!=='raw')assert.equal(r.closing,'ok:drained')
  assert.equal(count('checks'),c.order==='checks'?1:0,'only checks already entered may finish')
 }
 if(c.snapshotFault!=='raw'){
  const stopped=c.boundary==='provider',failed=c.snapshotFault==='typed'
  assert.match(r.retained,new RegExp(`^1,${stopped?'stopped':failed?'failed':'completed'},w1,base,0,false,${stopped||failed?'':'commit1'},${c.order==='checks'||c.order==='queued'?'passed':''},;`))
 }
 if(c.boundary!=='provider'){assert.equal(r['@snapshot:entry'],'clear');if(c.snapshotFault!=='raw')assert.equal(r['@snapshot'],'clear')}
 if(c.boundary==='provider')assert.equal(count('snapshot'),0)
 return row
}
