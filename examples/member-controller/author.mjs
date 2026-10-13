// One serialized graph controller; hosts expose effects/gates only.
import {author as lifecycle} from '../member-lifecycle/author.mjs'
import {defineIntegration} from '../member-integration/author.mjs'
import {defineTaskWaitObserver} from '../task-wait-observer/author.mjs'
import {string,int,bool,unit,unitValue,cap,scope,task,oneShot,list,option,result,named,p,pat,bind,field,construct,ok,err,direct,cond,matchOption,text,integer,boolean,concat,eqStr,eqInt,invoke,cancellation} from '../dynamic-worker-collector/lib.mjs'
const E=named('Entry'),S=named('State'),O=named('Outcome'),R=result(named('Final'))
const ctx=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
const A=(e,f)=>field(e,'Entry',f),T=(s,f)=>field(s,'State',f),F=(f,k)=>field(f,'Final',k)
const io=(operation,args={})=>invoke(p('host'),'Host',operation,args)
const mr=(input,okBody,errBody)=>({kind:'matchResult',input,okBody,errBody})
const L=(t,...items)=>({kind:'listConstruct',itemType:t,items})
const first=l=>({kind:'listItemAt',list:l,index:integer(0)})
const wait=(tasks,limit)=>({kind:'taskWait',tasks,mode:'all',...(limit?{limit:{kind:'optionSome',value:limit}}:{})})
const poll=t=>wait(L(task(R),A(t,'handle')),integer(0))
const update=(source,type,values)=>({kind:'productUpdate',source,type,fields:Object.entries(values).map(([field,value])=>({field,value}))})
const start=(worker,item,values)=>({kind:'taskStart',scope:p('scope'),worker,item,context:ctx(values)})
const shield=(body,values)=>({kind:'cancellationShield',body,context:ctx(values)})
const replies=(label,record)=>io('reply',{label,record})
const rendered=r=>mr(r,concat(text('ok:'),pat()),concat(text('err:'),pat('error')))

export async function define(s){
 await s.create('auditState',{host:cap('Host'),state:S,label:string},S,direct('view',{host:p('host'),state:p('state'),target:integer(0),mode:text('inspect'),label:concat(p('label'),text(':state'))}))
 await defineIntegration(s,{capability:'Host',shielded:true,rawBoundary:true})
 await defineTaskWaitObserver((...args)=>s.create(...args,[]))
 // Eligibility is derived from real handles and registry fields, never host flags.
 await s.create('candidateOf',{entry:E,settled:bool,status:string,commit:string},named('Candidate'),construct('Candidate',{
  settled:p('settled'),latest:{kind:'boolNot',value:A(p('entry'),'superseded')},discarded:A(p('entry'),'discarded'),status:p('status'),commit:p('commit'),integrated:A(p('entry'),'integrated')}),[])
 const pending=direct('candidateOf',{entry:p('entry'),settled:boolean(false),status:text('running'),commit:text('')})
 await s.create('candidate',{entry:E},named('Candidate'),bind(poll(p('entry')),matchOption(first(pat()),matchOption(pat(),mr(pat(),direct('candidateOf',{entry:p('entry'),settled:boolean(true),status:F(pat(),'status'),commit:F(pat(),'commit')}),direct('candidateOf',{entry:p('entry'),settled:boolean(true),status:text('failed'),commit:text('')})),pending),pending)),[])
 await s.create('integrationBody',{host:cap('Host'),entry:E,item:unit},result(O),
  bind(direct('integrate',{host:p('host'),candidate:direct('candidate',{entry:p('entry')})}),
   ok(update(pat(),'Outcome',{result:mr(field(pat(),'Outcome','result'),cond(cancellation,err(text('cancelled')),ok(pat())),err(pat('error')))}))))
 // The serialized request worker handles failure only after the protected tail
 // and rollback have completed. Raw faults therefore never poison the owner scope.
 await s.create('integrationFault',{entry:E,exception:{kind:'builtinGeneric',generic:'exception',arguments:[]}},result(O),
  ok(construct('Outcome',{candidate:direct('candidate',{entry:p('entry')}),result:err({kind:'exceptionMessage',value:p('exception')})})),[])
 await s.create('integrationWorker',{host:cap('Host'),entry:E,item:unit},result(O),
  {kind:'exceptionCatch',body:'integrationBody',bodyContext:ctx({host:p('host'),entry:p('entry')}),handler:'integrationFault',handlerContext:ctx({entry:p('entry')})})
 await s.create('ledgerEntry',{target:int,head:string,item:E},E,
  cond(eqInt(A(p('item'),'id'),p('target')),update(p('item'),'Entry',{integrated:p('head')}),p('item')),[])
 await s.create('acceptIntegration',{host:cap('Host'),state:S,target:int,label:string,outcome:O},S,
  bind(replies(p('label'),rendered(field(p('outcome'),'Outcome','result'))),
   update(p('state'),'State',{entries:{kind:'listMap',source:T(p('state'),'entries'),function:'ledgerEntry',context:ctx({target:p('target'),head:field(field(p('outcome'),'Outcome','candidate'),'Candidate','integrated')})}})))
 const it=task(result(O))
 await s.create('joinIntegration',{host:cap('Host'),state:S,target:int,label:string,mode:string,parent:it},S,
  bind(cond(eqStr(p('mode'),text('cancel')),
   bind(io('gate',{label:text('integration-await')}),bind({kind:'taskCancel',task:p('parent')},
    bind({kind:'taskCancel',task:p('parent')},bind(replies(concat(p('label'),text(':pending')),matchOption(first(wait(L(it,p('parent')),integer(0))),matchOption(pat(),text('settled'),text('pending')),text('missing'))),io('gate',{label:text('integration-release')}))))),unitValue),
   matchOption(first(wait(L(it,p('parent')))),matchOption(pat(),mr(pat(),direct('acceptIntegration',{host:p('host'),state:p('state'),target:p('target'),label:p('label'),outcome:pat()}),p('state')),p('state')),p('state'))))
 await s.create('integrateRequest',{host:cap('Host'),scope,state:S,target:int,label:string,mode:string},S,
  matchOption(direct('get',{state:p('state'),target:p('target')}),
   bind(start('integrationWorker',unitValue,{host:p('host'),entry:pat()}),direct('joinIntegration',{host:p('host'),state:p('state'),target:p('target'),label:p('label'),mode:p('mode'),parent:pat()})),
   bind(replies(p('label'),text('err:unknown')),p('state'))))
 // The observer and monitor share the owner; cancelling the observer leaves Members alive.
 const states=list(option(R)),obs=result(states)
 await s.create('coordinateObserver',{host:cap('Host'),state:S,label:string,caller:task(obs),monitor:task(obs)},S,
  bind(io('gate',{label:text('observation-await')}),bind({kind:'taskCancel',task:p('caller')},
   bind(direct('superviseTaskObservation',{caller:p('caller'),monitor:p('monitor')}),
    bind(wait(L(task(obs),p('caller'))),
     bind(replies(p('label'),matchOption(first(pat()),matchOption(pat(),mr(pat(),text('ok:observed'),concat(text('err:'),pat('error'))),text('pending')),text('missing'))),p('state')))))))
 // Observation success renders a string instead of generic list; Err remains exact.
 // Separate renderer handles only the success tag; normal waits still expose every Member.
 await s.create('observeStarted',{host:cap('Host'),scope,state:S,label:string,signal:oneShot(states),monitor:task(obs)},S,
  bind(start('awaitTaskObservation',unitValue,{signal:p('signal'),limit:{kind:'optionNone',itemType:int},noValueError:text('cancelled')}),
   direct('coordinateObserver',{host:p('host'),state:p('state'),label:p('label'),caller:pat(),monitor:p('monitor')})))
 await s.create('observeRequest',{host:cap('Host'),scope,state:S,label:string},S,
  bind({kind:'oneShotCreate',scope:p('scope'),valueType:states},
   bind(start('observeTasks_all',unitValue,{signal:pat(),limit:{kind:'optionNone',itemType:int},tasks:{kind:'listMap',source:T(p('state'),'entries'),function:'handleOf'}}),
    direct('observeStarted',{host:p('host'),scope:p('scope'),state:p('state'),label:p('label'),signal:pat('value',1),monitor:pat()}))))
 // Shield finish in its own request worker. Explicit stop/drain remains graph policy.
 await s.create('finishProtected',{host:cap('Host'),state:S,label:string,item:unit},result(S),ok(direct('finish',{host:p('host'),state:p('state'),label:p('label')})))
 await s.create('finishWorker',{host:cap('Host'),state:S,label:string,item:unit},result(S),bind(shield('finishProtected',{host:p('host'),state:p('state'),label:p('label')}),bind(replies(concat(p('label'),text(':outcome')),cond(cancellation,text('err:cancelled'),text('ok:drained'))),pat('value',1))))
 const ft=task(result(S))
 await s.create('joinFinish',{host:cap('Host'),state:S,label:string,mode:string,parent:ft},S,
  bind(cond(eqStr(p('mode'),text('cancel')),bind(io('gate',{label:text('finish-await')}),bind({kind:'taskCancel',task:p('parent')},bind({kind:'taskCancel',task:p('parent')},bind(replies(concat(p('label'),text(':pending')),matchOption(first(wait(L(ft,p('parent')),integer(0))),matchOption(pat(),text('settled'),text('pending')),text('missing'))),io('gate',{label:text('finish-release')}))))),unitValue),
   matchOption(first(wait(L(ft,p('parent')))),matchOption(pat(),mr(pat(),pat(),p('state')),p('state')),p('state'))))
 await s.create('finishRequest',{host:cap('Host'),scope,state:S,label:string,mode:string},S,
  bind(start('finishWorker',unitValue,{host:p('host'),state:p('state'),label:p('label')}),direct('joinFinish',{host:p('host'),state:p('state'),label:p('label'),mode:p('mode'),parent:pat()})))
}
export const extension={continueBody:(body,reject)=>cond({kind:'boolOr',left:A(p('prior'),'superseded'),right:A(p('prior'),'discarded')},body,cond(eqStr(A(p('prior'),'integrated'),text('')),body,reject('integrated'))),entryFields:{integrated:string},entryValues:{integrated:text('')},renderSuffix:e=>A(e,'integrated'),
 operations:[['step',{operation:string,patch:string},result(list(string))]],define,
 dispatch:(fallback,{args,req,label,target})=>cond(eqStr(field(req,'Request','op'),text('integrate')),direct('integrateRequest',{...args,target,label,mode:field(req,'Request','mode')}),
  cond({kind:'boolAnd',left:eqStr(field(req,'Request','op'),text('gate')),right:eqStr(field(req,'Request','mode'),text('settle'))},bind(io('gate',{label}),direct('wait',{host:p('host'),state:p('state'),target,mode:text('all'),limit:integer(5000),label})),
  cond(eqStr(field(req,'Request','op'),text('observe')),direct('observeRequest',{...args,label}),
   cond(eqStr(field(req,'Request','op'),text('finish')),direct('finishRequest',{...args,label,mode:field(req,'Request','mode')}),fallback))))}
export const author=(s,{protectDetermined=false,containSnapshotFaults=false}={})=>lifecycle(s,{...extension,protectDetermined,containSnapshotFaults})
