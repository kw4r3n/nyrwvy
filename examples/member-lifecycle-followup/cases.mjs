import assert from 'node:assert/strict'
const prim=primitive=>({kind:'primitive',primitive}),unit=prim('unit'),str=prim('string')
const u={kind:'unit'},sv=value=>({kind:'string',value}),iv=value=>({kind:'int64',value:String(value)})
const ret=(value,delay=0)=>({kind:'return',value,...(delay?{delayMillis:String(delay)}:{})})
const flat=(isOk,value)=>({kind:'result',okKind:'string',errorKind:'string',isOk,value})
const unitOk={kind:'result',okType:unit,errorType:str,isOk:true,value:u}
const observation=(kind,cause='')=>({kind:'result',okType:{kind:'named',name:'ProviderObservation'},errorType:str,isOk:true,value:{kind:'product',type:'ProviderObservation',fields:[{field:'kind',value:sv(kind)},{field:'cause',value:sv(cause)}]}})
const cancelled={kind:'result',okType:{kind:'named',name:'ProviderObservation'},errorType:str,isOk:false,value:sv('cancelled')}
export function scenarios(){return [
 {id:'any',mode:'any',limit:-1,providers:['complete','hold']},
 {id:'all',mode:'all',limit:-1,providers:['complete','complete']},
 {id:'timeout',mode:'timeout',limit:5,providers:['hold','hold']},
 {id:'timeout-zero',mode:'timeout',limit:0,providers:['hold','hold']},
 {id:'caller-cancel',mode:'caller-cancel',limit:-1,providers:['hold','hold']},
 ...['race-cancel-first','race-complete-first','race-published-cancel'].map(mode=>({id:mode,mode,limit:-1,providers:['complete','complete']})),
 {id:'snapshot-timeout',mode:'snapshot-timeout',limit:5,providers:['complete','hold']},
 {id:'tagged-isolation',mode:'tagged-isolation',limit:-1,providers:['unexpected','hold']},
 {id:'tagged-expected',mode:'tagged-expected',limit:-1,providers:['expected','hold']},
 ...['global-exit','global-external'].map(mode=>({id:mode,mode,limit:-1,providers:['hold','hold'],global:true})),
 ...['cleanup-fault','outside-provider-fault','foreign-cancellation'].map(id=>({id,mode:'any',limit:-1,providers:['complete','hold'],fault:true})),
]}
export function argumentsFor(c){
 const repeated=value=>Array.from({length:50},()=>ret(value))
 const gates=['members-ready','waiter-ready','monitor-entered','monitor-published','caller-entered','caller-observed','release-first','release-all','release-peer','release-caller','observation-ready','release-snapshot','snapshot1','global-arm','stop-join:1','stop-join:2']
 const host={kind:'fakeCapability',capability:'Host',operations:[
  {operation:'open',responses:[ret(unitOk)]},{operation:'close',responses:[ret(u)]},
  {operation:'workspaceCreate',responses:repeated(unitOk)},{operation:'workspaceDiscard',responses:repeated(unitOk)},
  {operation:'provider',responses:repeated(flat(false,'unused legacy provider'))},
  {operation:'memberClose',cases:[1,2].map(id=>({arguments:[{parameter:'id',value:iv(id)}],responses:[c.id==='cleanup-fault'&&id===1?{kind:'throw',message:'cleanup fault'}:ret(u)]}))},
  {operation:'snapshot',cases:[1,2].map(id=>({arguments:[{parameter:'id',value:iv(id)},{parameter:'workspace',value:sv(`w${id}`)}],responses:[ret(flat(true,`commit${id}`),c.id==='snapshot-timeout'&&id===1?100:1)]}))},
  {operation:'checks',responses:repeated(sv('passed'))},
  {operation:'gate',cases:gates.map(label=>({arguments:[{parameter:'label',value:sv(label)}],responses:Array.from({length:5},()=>c.id==='race-published-cancel'&&label==='caller-observed'?{kind:'awaitCancellation',value:u}:ret(u,['release-snapshot','release-peer'].includes(label)?110:['members-ready','waiter-ready','observation-ready','snapshot1'].includes(label)?1:0))}))},
  {operation:'reply',responses:repeated(u)},
 ]}
 const provider={kind:'fakeCapability',capability:'TaggedProvider',operations:[{operation:'run',cases:c.providers.map((kind,index)=>({arguments:[{parameter:'id',value:iv(index+1)},{parameter:'workspace',value:sv(`w${index+1}`)}],responses:[
  index===0&&['outside-provider-fault','foreign-cancellation'].includes(c.id)?{kind:'throw',message:c.id==='foreign-cancellation'?'foreign cancellation':'outside provider run'}:
  kind==='hold'&&!(index===1&&c.id.startsWith('tagged-'))?{kind:'awaitCancellation',value:cancelled}:
  ret(observation(kind==='unexpected'?'unexpected-provider-error':kind==='expected'?'expected-provider-error':'completed',kind==='unexpected'?'unexpected host fault':kind==='expected'?'deadline':''),c.id==='snapshot-timeout'?1:index===1&&c.id.startsWith('tagged-')?100:index===0?10:12),
 ]}))}]}
 return [host,provider,sv(c.mode),iv(c.limit)]
}
export const arg=(call,name)=>call.arguments.find(a=>a.parameter===name)?.value?.value
export function validate(c,response){
 assert.equal(response.ok,!c.fault,JSON.stringify(response).slice(0,2000))
 const calls=c.fault?response.error.calls:response.result.calls
 assert.equal(calls.filter(x=>x.operation==='open').length,1)
 assert.equal(calls.filter(x=>x.operation==='close').length,1)
 const closeAt=calls.findIndex(x=>x.operation==='close')
 assert.ok(calls.every((x,i)=>!['taggedRun','run','memberClose','snapshot','checks'].includes(x.operation)||i<closeAt),'resource closes after worker effects')
 const replies=Object.fromEntries(calls.filter(x=>x.operation==='reply').map(x=>[arg(x,'label'),arg(x,'record')]))
 assert.equal(calls.filter(x=>x.operation==='workspaceDiscard').length,0)
 assert.equal(calls.filter(x=>x.operation==='workspaceCreate').length,2)
 for(const id of [1,2])assert.equal(calls.filter(x=>x.operation==='memberClose'&&arg(x,'id')===String(id)).length,1,'lease closes once even on raw fault')
 if(c.fault){assert.equal(response.error.code,'RunRuntimeFailure');assert.equal(replies['provider-evidence:1'],undefined);assert.equal(replies.final,undefined);return {id:c.id,scopeFaultPreserved:true,replies}}
 assert.equal(response.result.actual.isOk,!c.global,'global cancellation remains visible as Err')
 const completed=id=>`${id},completed,w${id},base,0,false,commit${id},passed;`
 const stopped=id=>`${id},stopped,w${id},base,0,false,,;`
 const failed=id=>`${id},failed,w${id},base,0,false,,;`
 if(c.global){assert.equal(replies.final,stopped(1)+stopped(2));assert.equal(replies['monitor-delivery'],'closed');return {id:c.id,replies,globalCancellationVisible:true}}
 const isCancelled=c.id==='caller-cancel'||['race-cancel-first','race-published-cancel'].includes(c.id)
 assert.equal(replies.caller,isCancelled?'caller:cancelled':'caller:observed')
 if(['timeout','timeout-zero','caller-cancel','snapshot-timeout'].includes(c.id)){assert.equal(replies.observed,'1,running;2,running;');assert.equal(replies.occupied,'2')}
 else if(['any','tagged-isolation','tagged-expected'].includes(c.id)){assert.equal(replies.observed,(c.id==='any'?completed(1):failed(1))+'2,running;');assert.equal(replies.occupied,'1')}
 else {assert.equal(replies.observed,completed(1)+completed(2));assert.equal(replies.occupied,'0')}
 if(isCancelled)assert.equal(replies['observer-payload'],undefined)
 else assert.equal(replies['observer-payload'],['timeout','timeout-zero','snapshot-timeout'].includes(c.id)?'1,running;2,running;':['any','tagged-isolation','tagged-expected'].includes(c.id)?`1,${c.id==='any'?'completed':'failed'};2,running;`:'1,completed;2,completed;','actual monitor packet, not a fresh task poll')
 if(c.id==='caller-cancel')assert.equal(replies.monitor,'monitor:running','caller exits before monitor or Member settles')
 if(c.id.startsWith('tagged-')){assert.equal(replies['provider-evidence:1'],c.id==='tagged-isolation'?'unexpected-provider-error|unexpected host fault':'expected-provider-error|deadline');assert.equal(replies.final,failed(1)+completed(2))}
 else if(['all',...['race-cancel-first','race-complete-first','race-published-cancel']].includes(c.id))assert.equal(replies.final,completed(1)+completed(2))
 else assert.equal(replies.final,['any','snapshot-timeout'].includes(c.id)?completed(1)+stopped(2):stopped(1)+stopped(2))
 assert.equal(replies['monitor-delivery'],'accepted')
 return {id:c.id,replies,callerCancellationIsolated:true,workspacesRetained:true}
}
