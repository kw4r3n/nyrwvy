import assert from 'node:assert/strict'
import {request as q} from '../member-lifecycle/cases.mjs'
import {argumentsFor as inherited,arg} from '../member-owner-cancellation/cases.mjs'
export const scenarios=()=>[
 {id:'determined-snapshot',boundary:'snapshot'},
 {id:'determined-raw-snapshot',boundary:'snapshot',snapshotFault:'raw'},
 {id:'determined-typed-snapshot',boundary:'snapshot',snapshotFault:'typed'},
 {id:'determined-cleanup',boundary:'cleanup'},
 {id:'determined-failed-cleanup',boundary:'cleanup',providerFailure:true},
 {id:'undetermined-provider',boundary:'provider'},
 {id:'cancel-before-completed-provider-return',boundary:'provider',completedAfterCancel:true},
 {id:'undetermined-entry',boundary:'entry'},
 {id:'settled-before-cancel',boundary:'settled'},
 {id:'determined-release-fault',boundary:'snapshot',releaseFault:true},
].map(c=>({...c,kind:'boundary',patch:'synthetic-patch',providers:{1:['provider','entry'].includes(c.boundary)?'hold':'complete'},script:[q('spawn','a'),q('gate',c.boundary+'1'),...(c.boundary==='settled'?[q('wait','before-cancel',1)]:[]),q('gate','owner-park'),q('wait','settled',1),q('inspect','retained')]}))
export function argumentsFor(c){
 const args=inherited({...c,kind:'integration'}),host=args[0],op=name=>host.operations.find(x=>x.operation===name)
 op('gate').cases=[]
 const labels=new Set([...c.script.filter(x=>x.op==='gate').map(x=>x.label),...c.script.map(x=>'request-start:'+x.label),'worker-entry:1','enter:a','released:a','probe-ready','probe-cancelled','probe-release'])
 for(const label of labels){
  const delay=label===c.boundary+'1'?(c.boundary==='entry'?1:c.boundary==='settled'?700:120):label==='probe-ready'?130:label==='probe-release'?150:0
  op('gate').cases.push({arguments:[{parameter:'label',value:{kind:'string',value:label}}],responses:Array.from({length:4},()=>label==='owner-park'?{kind:'awaitCancellation',value:{kind:'unit'}}:{kind:'return',value:{kind:'unit'},...(delay?{delayMillis:String(delay)}:{})})})
 }
 if(c.boundary==='entry')op('gate').cases.find(x=>x.arguments[0].value.value==='worker-entry:1').responses.forEach(x=>x.delayMillis='500')
 op('memberClose').responses=[{kind:'return',value:{kind:'unit'},...(c.boundary==='cleanup'?{delayMillis:'500'}:{})}]
 op('provider').cases=op('provider').cases.filter(x=>x.arguments.some(a=>a.parameter==='id'&&a.value.value==='1'))
 if(c.providerFailure)op('provider').cases[0].responses=[{kind:'return',value:{kind:'result',okKind:'string',errorKind:'string',isOk:false,value:'provider failure'}}]
 if(c.completedAfterCancel)op('provider').cases[0].responses=[{kind:'awaitCancellation',value:{kind:'result',okKind:'string',errorKind:'string',isOk:true,value:'completed'}}]
 op('snapshot').cases=op('snapshot').cases.filter(x=>x.arguments.some(a=>a.parameter==='id'&&a.value.value==='1'))
 op('snapshot').cases[0].responses=[c.snapshotFault==='raw'?{kind:'throw',message:'raw snapshot fault',delayMillis:'500'}:{kind:'return',value:{kind:'result',okKind:'string',errorKind:'string',isOk:c.snapshotFault!=='typed',value:c.snapshotFault==='typed'?'snapshot failure':'commit1'},delayMillis:c.boundary==='snapshot'?'500':'20'}]
 // Probe token records use the same reply operation as ordinary recipes.
 op('reply').responses=Array.from({length:30},()=>({kind:'return',value:{kind:'unit'}}))
 return args
}
export function normalize(c,response){
 const calls=response.ok?response.result.calls:response.error.calls
 const replies=calls.filter(x=>x.operation==='reply').map(x=>({label:arg(x,'label'),record:arg(x,'record')}))
 return {id:c.id,outcome:response.ok?(response.result.actual.isOk?'ok':'err:'+(response.result.actual.error??response.result.actual.value.value)):'error:'+response.error.message,replies,effects:calls.filter(x=>['provider','memberClose','snapshot','checks','close'].includes(x.operation)).map(x=>x.operation),released:calls.filter(x=>x.operation==='close').length}
}
export function acceptance(c,row){
 assert.equal(row.released,1);assert.equal(row.effects.filter(x=>x==='memberClose').length,c.boundary==='entry'?0:1)
 assert.equal(row.effects.at(-1),'close','settlement drains before release')
 const replies=Object.fromEntries(row.replies.map(x=>[x.label,x.record]))
 assert.equal(replies.a,'spawn:1')
 if(c.boundary==='settled'){assert.match(replies['before-cancel'],/^1,completed,w1,base,0,false,commit1,passed,;/);assert.equal(replies['before-cancel'],replies.retained)}
 const cancelled=['provider','entry'].includes(c.boundary)
 if(c.boundary!=='entry')assert.equal(replies['@memberClose'],'clear','cleanup uses a masked token after outcome capture')
 if(!cancelled&&!c.providerFailure){assert.equal(replies['@snapshot:entry'],'clear');if(c.snapshotFault!=='raw')assert.equal(replies['@snapshot'],'clear','determined snapshot survives owner cancellation')}
 assert.equal(row.outcome,c.snapshotFault==='raw'?'error:raw snapshot fault':c.releaseFault?'error:release fault':'err:owner cancelled')
 if(c.snapshotFault!=='raw')assert.match(replies.retained,new RegExp(`1,${cancelled?'stopped':c.providerFailure||c.snapshotFault==='typed'?'failed':'completed'},w1,base,0,false,${cancelled||c.providerFailure||c.snapshotFault==='typed'?'':'commit1'},`))
 if(cancelled||c.providerFailure)assert.ok(!row.effects.includes('snapshot'))
 return row
}
