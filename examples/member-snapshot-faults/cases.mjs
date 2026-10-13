import assert from 'node:assert/strict'
import {request as q} from '../member-lifecycle/cases.mjs'
import {argumentsFor as heldArgs,normalize} from '../member-determined-settlement/cases.mjs'
import {argumentsFor as closeArgs,scenarios as closeCases} from '../member-close-intent/cases.mjs'
export {normalize}
export const scenarios=()=>[
 {id:'ordinary-raw-snapshot',snapshotFault:'raw',function:'concurrentRun'},
 {id:'held-raw-owner-cancel',snapshotFault:'raw'},
 {id:'admitted-close-raw-snapshot',snapshotFault:'raw',order:'admitted'},
 {id:'raw-snapshot-release-precedence',snapshotFault:'raw',releaseFault:true},
 {id:'typed-snapshot-failure',snapshotFault:'typed',function:'concurrentRun'},
 {id:'successful-snapshot-checks',function:'concurrentRun'},
 {id:'successful-snapshot-close',order:'admitted'},
 {id:'successful-snapshot-release-fault',order:'admitted',releaseFault:true},
 {id:'raw-checks-fault',checksFault:true,boundary:'checks',function:'concurrentRun'},
 {id:'undetermined-provider-cancel',boundary:'provider'},
 {id:'explicit-snapshot-oce',snapshotFault:'oce',function:'concurrentRun'},
].map(c=>({...c,boundary:c.boundary??'snapshot',order:c.order??'none',kind:'snapshot-faults',patch:'synthetic-patch',providers:{1:c.boundary==='provider'?'hold':'complete'},script:c.order==='admitted'?
 closeCases().find(x=>x.id==='cancel-before-close-publication').script:
 [q('spawn','a'),q('gate',(c.boundary??'snapshot')+'1'),q('gate',c.function?'release-effect':'owner-park'),q('wait','settled',1),q('inspect','retained')]}))
export function argumentsFor(c){
 const args=c.order==='admitted'?closeArgs(c):heldArgs(c),host=args[0],op=name=>host.operations.find(x=>x.operation===name)
 if(c.function){
  op('snapshot').cases[0].responses.forEach(x=>x.delayMillis='500')
 }
 // runPure's synthetic throw is an ordinary host exception. The actual C#
 // host separately throws an explicit OCE; do not label virtual class parity.
 if(c.snapshotFault==='oce')op('snapshot').cases[0].responses=[{kind:'throw',message:'explicit snapshot cancellation',delayMillis:'500'}]
 if(c.checksFault)op('checks').cases[0].responses=[{kind:'throw',message:'raw checks fault'}]
 return args
}
export function acceptance(c,row){
 const r=Object.fromEntries(row.replies.map(x=>[x.label,x.record])),count=op=>row.effects.filter(x=>x===op).length
 assert.equal(row.released,1,'one release attempt');assert.equal(row.effects.at(-1),'close','workers drain before release')
 assert.equal(count('memberClose'),1,'cleanup exactly once');assert.equal(r.a,'spawn:1');assert.equal(r['@memberClose'],'clear')
 assert.equal(row.outcome,c.releaseFault?'error:release fault':c.checksFault?'error:raw checks fault':c.function?'ok':'err:owner cancelled','bounded snapshot fault is contained; outside faults retain precedence')
 const failed=!!c.snapshotFault,stopped=c.boundary==='provider'
 assert.equal(count('snapshot'),stopped?0:1);assert.equal(count('checks'),failed||stopped||c.order==='admitted'?0:1,'failed snapshot skips checks')
 if(c.order==='admitted'){assert.equal(r['@close-publication'],'accepted');assert.equal(r.closing,'ok:drained')}
 if(!c.checksFault){
  assert.match(r.settled,new RegExp(`^1,${stopped?'stopped':failed?'failed':'completed'},w1,base,0,false,${stopped||failed?'':'commit1'},${!failed&&!stopped&&c.order!=='admitted'?'passed':''},;`),'joined immutable Final retained')
  assert.equal(r.retained,r.settled,'inspection follows explicit join')
 }
 if(!stopped){assert.equal(r['@snapshot:entry'],'clear');if(!['raw','oce'].includes(c.snapshotFault))assert.equal(r['@snapshot'],'clear')}
 return row
}
