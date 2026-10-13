import assert from 'node:assert/strict'
import {request as q} from '../member-lifecycle/cases.mjs'
import {argumentsFor as admissionArgs} from '../member-concurrency/cases.mjs'
const begin=(op,label,slot,member=0)=>q('begin',label,slot,{mode:op,previous:member})
const join=slot=>q('join',`join${slot}`,slot),gate=label=>q('gate',label)
const turn=(label,...commands)=>({label,commands})
export const terminal='1,completed,w1,base,0,false,commit1,passed,integrated-head;2,stopped,w2,base,0,false,,,;'
export const scenarios=()=>[
 ...['throw','none'].map(cancellation=>({id:`leader-session-${cancellation}`,kind:'session',cancellation,patch:'synthetic-patch',providers:{1:'complete',2:'hold'},turns:[
  turn('one',q('spawn','a'),gate('provider1')),
  turn('two',q('spawn','b'),gate('provider2'),gate('snapshot1'),q('inspect','progress'),gate('release1'),q('wait','settled-a',1),begin('integrate','first',1,1),gate('commit-held')),
  turn('three',begin('spawn','cancelled',2),gate('queued:cancelled'),q('cancel','cancel2',2),join(2),q('inspect','peer-alive',2),
   begin('wait','waiting',3,2),begin('stop','stopping',4,2),join(3),join(4),begin('integrate','second',5,1),gate('queued:second'),gate('commit-release'),join(1),join(5),q('inspect','ledger',1)),
  turn('four',begin('finish','closing',6),gate('close-held'),begin('spawn','late',7),join(7),gate('close-release'),join(6)),
 ]})),
 {id:'command-ordinary-vs-release',kind:'command-fault',releaseFault:'ordinary',providers:{1:'hold'},turns:[turn('fault',q('spawn','a'),gate('provider1'),begin('spawn','fault',1),gate('queued:fault'),gate('fault-release'),join(1))]},
 {id:'member-ordinary-vs-release',kind:'member-fault',releaseFault:'ordinary',providers:{1:'fault'},turns:[turn('fault',q('spawn','a'),gate('provider1'),q('wait','faulting-member',1))]},
 ...['ordinary','OCE'].map(cleanupFault=>({id:`cleanup-${cleanupFault}-vs-release`,kind:'cleanup-fault',cleanupFault,releaseFault:'ordinary',cancellation:'throw',providers:{1:'hold'},turns:[
  turn('cleanup',q('spawn','a'),gate('provider1'),begin('spawn','blocker',1),gate('spawn-held'),begin('spawn','cancelled',2),gate('queued:cancelled'),q('cancel','cancel2',2),gate('cleanup-held'),gate('spawn-release'),gate('cleanup-release'),join(2))
 ]})),
 {id:'release-OCE-outside-adapter',kind:'release-fault',releaseFault:'OCE',providers:{},turns:[]},
]
export const arg=(call,key)=>call.arguments.find(a=>a.parameter===key)?.value?.value
export function acceptance(c,row){
 assert.equal(row.response.ok,true,JSON.stringify(row.response.error))
 assert.equal(row.response.result.actual.isOk,true)
 assert.equal(row.response.result.actual.value,terminal,'checked terminal Result')
 const calls=row.response.result.calls,indices=(operation,key,value)=>calls.flatMap((x,i)=>x.operation===operation&&(!key||arg(x,key)===value)?[i]:[])
 const at=(op,key,value)=>{const found=indices(op,key,value);assert.ok(found.length,`${op}:${value}`);return found[0]}
 const replies=Object.fromEntries(calls.filter(x=>x.operation==='reply').map(x=>[arg(x,'label'),arg(x,'record')]))
 assert.equal(replies.a,'spawn:1');assert.equal(replies.b,'spawn:2')
 assert.equal(replies.first,'ok:integrated-head');assert.equal(replies.second,replies.first)
 assert.equal(replies['cancelled:outcome'],'err:command-cancelled')
 assert.equal(replies.late,'reject:closing');assert.equal(replies.terminal,terminal)
 assert.equal(replies['@leader:one'],'')
 assert.match(replies['@leader:two'],/^1,(running|completed),/)
 assert.match(replies.progress,/2,running/)
 assert.match(replies['peer-alive'],/^2,running,/)
 for(const label of ['waiting','stopping']){
  assert.equal(replies[label],'2,stopped,w2,base,0,false,,,;')
  assert.ok(at('reply','label',label)<at('gate','label','commit-release'),'independent wait/stop remain responsive during held integration')
 }
 assert.ok(at('reply','label','cancelled:outcome')<at('gate','label','commit-release'),'queued outcome before ownership release')
 assert.equal(indices('workspaceCreate').length,2,'cancelled and closed admissions have no workspace effects')
 assert.equal(indices('gate','label','enter:cancelled').length,0,'cancelled command never admitted')
 assert.ok(calls.filter(x=>x.request==='cancelled').every(x=>!['workspaceCreate','provider','step'].includes(x.operation)))
 for(const phase of ['check','apply','commit','head','save'])assert.equal(indices('step','operation',phase).length,1,`${phase} exactly once`)
 assert.ok(calls.filter(x=>x.operation==='step').every(x=>x.request===undefined||x.request==='first'),'per-request integration ownership')
 assert.equal(calls.filter(x=>x.operation==='step'&&x.request==='second').length,0,'duplicate integration returns retained HEAD without effects')
 assert.ok(at('step','operation','save')<at('gate','label','enter:second'),'retained HEAD published before duplicate entry')
 assert.ok(at('reply','label','late')<at('gate','label','close-release'),'cutoff rejects while close drain held')
 assert.ok(at('reply','label','@requests-joined')<at('reply','label','@terminal-inspected'),'all request workers/supervisors joined before terminal inspection')
 assert.ok(at('reply','label','@terminal-inspected')<at('close'),'terminal inspection stays inside the joined owner scope')
 assert.ok(at('close')<at('reply','label','terminal'),'success publication follows once-only outer release')
 assert.equal(indices('open').length,1);assert.equal(indices('close').length,1)
 assert.ok(calls.every((x,i)=>!['workspaceCreate','provider','memberClose','snapshot','checks','step'].includes(x.operation)||i<at('close')))
 for(const id of ['1','2']){
  assert.equal(indices('provider','id',id).length,1)
  assert.equal(indices('memberClose','id',id).length,1)
  assert.ok(at('memberClose','id',id)<at('reply','label','terminal'))
 }
 assert.equal(indices('snapshot').length,1);assert.equal(indices('checks').length,1)
 return {id:c.id,terminal,replies,workspaceEffects:2,integrationEffects:indices('step').length,releaseCount:1}
}

// Supplementary virtual scheduling. Runtime gates below are the decisive proof.
export function argumentsFor(c){
 const script=c.turns.flatMap(t=>t.commands)
 const [host]=admissionArgs({...c,kind:'wait-stop',script})
 const op=name=>host.operations.find(x=>x.operation===name)
 const gateLabels=[...new Set([...script.filter(x=>x.op==='gate').map(x=>x.label),...script.filter(x=>!['gate','join','cancel'].includes(x.op)).flatMap(x=>['request-start:','enter:','released:','command-cleanup:'].map(pre=>pre+x.label)),...['request-start:','enter:','released:','command-cleanup:'].map(pre=>pre+'@auto-close'),'worker-entry:1','worker-entry:2','stop-join:1','stop-join:2','close-held-worker'])]
 const delays={'provider1':1,'provider2':1,'snapshot1':30,'release1':30,'commit-held':2,'commit-release':300,'close-held':1,'close-held-worker':100,'close-release':110}
 op('gate').cases=gateLabels.map(label=>({arguments:[{parameter:'label',value:{kind:'string',value:label}}],responses:Array.from({length:label.startsWith('stop-join:')?4:2},()=>({kind:'return',value:{kind:'unit'},...(delays[label]?{delayMillis:String(delays[label])}:{})}))}))
 const val=(type,fields)=>({kind:'product',type,fields:Object.entries(fields).map(([field,value])=>({field,value}))})
 const request=r=>val('Request',Object.fromEntries(Object.entries(r).map(([k,v])=>[k,typeof v==='number'?{kind:'int64',value:String(v)}:{kind:'string',value:v}])))
 return [host,{kind:'list',itemType:{kind:'named',name:'LeaderTurn'},items:c.turns.map(t=>val('LeaderTurn',{label:{kind:'string',value:t.label},commands:{kind:'list',itemType:{kind:'named',name:'Request'},items:t.commands.map(request)}}))},{kind:'bool',value:false}]
}
