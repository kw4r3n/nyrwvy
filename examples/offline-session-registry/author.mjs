// Durable application continuation composed from ordinary graph primitives.
import {author as journalAuthor,maxFrameBytes} from '../offline-session-journal/author.mjs'
import {string,int,bool,unit,unitValue,option,result,ok,err,pat,cap,scope,named,oneShot,task,list,p,field,construct,L,direct,invoke,text,eqStr,eqInt,integer,boolean,cond,concat as rawConcat,letIn,some,resolveFrames,and,or,not,ctx,fold,lt} from '../offline-leader-jsonrpc/recipes.mjs'
export {maxFrameBytes}
const S=named('State'),E=named('Entry'),W=named('AdmissionOwner'),D=named('WireDecoded'),J=named('WireJson'),F=named('Final'),K=named('AdmissionTicket')
const f=(value,type,key)=>field(value,type,key),io=(op,args)=>invoke(p('host'),'Host',op,args)
const get=(value,key)=>f(direct('wireGet',{value,key:text(key)}),'WireJson','scalar')
const json=(value,key)=>direct('wireGet',{value,key:text(key)})
const kind=(value,type)=>eqStr(f(value,'WireJson','kind'),text(type))
const sf=(value,key)=>kind(json(value,key),'string')
const balanced=(values,combine)=>values.length===1?values[0]:combine(balanced(values.slice(0,Math.floor(values.length/2)),combine),balanced(values.slice(Math.floor(values.length/2)),combine))
const concat=(...values)=>balanced(values,rawConcat),all=values=>balanced(values,and),oneOf=(value,values)=>balanced(values.map(x=>eqStr(value,text(x))),or)
const quote=value=>direct('wireQuote',{value}),num=value=>({kind:'int64ToString',value}),yes=value=>cond(value,text('true'),text('false'))
const parse=source=>direct('journalParse',{source}),integerOf=value=>some({kind:'stringParseInt64',value},x=>x,integer(-1))
const update=(source,type,fields)=>({kind:'productUpdate',source,type,fields:Object.entries(fields).map(([field,value])=>({field,value}))})
const createCell=valueType=>({kind:'oneShotCreate',scope:p('scope'),valueType})
const complete=(cell,value)=>({kind:'oneShotComplete',cell,value})
const wait=handle=>({kind:'taskWait',tasks:L(task(result(F)),handle),mode:'all'})
const rendered=outcome=>({kind:'matchResult',input:f(outcome,'Outcome','result'),okBody:concat(text('ok:'),pat()),errBody:concat(text('err:'),pat('error'))})
const registryKey=text('registry/v1'),read=()=>io('storageRead',{key:registryKey})
const memberKey=id=>concat(text('registry-member/v1/'),num(id))
const admissionKey=id=>concat(text('registry-admission/v1/'),id)
const valueFields=['id','workspace','base','previous','discarded','superseded','status','commit','checks','settled','integrated','admission','fingerprint']
const envelope=(phase,state,admission,outcome)=>concat(text('{"version":"1","phase":'),quote(text(phase)),text(',"id":'),quote(get(admission,'id')),text(',"token":'),quote(get(admission,'token')),text(',"fingerprint":'),quote(get(admission,'fingerprint')),text(',"intent":'),quote(get(admission,'intent')),text(',"outcomeSource":'),quote(text(phase==='ready'?'actual':'')),text(',"outcome":'),quote(outcome),text(',"next":'),quote(num(f(state,'State','next'))),text(',"entries":['),direct('registryEntries',{host:p('host'),state}),text(']}'))

export async function author(s,{restorationDisabled=false}={}){
 const make=(name,ps,type,body,effects)=>s.create(name,ps,type,resolveFrames(body),effects)
 const inherited={...s,call:async(name,args)=>{
  if(name==='request'&&args.op==='simulateCreateCapabilityByName'&&args.arguments.displayName==='Host')args={...args,arguments:{...args.arguments,operations:[...args.arguments.operations,{displayName:'storageEmpty',parameters:[],resultType:bool,effect:'Io'}]}}
  return s.call(name,args)
 },create:async(name,...args)=>{
  if(name==='journalAcceptProtected')await s.create('registryAcceptOriginal',...args)
  return s.create(name,...args)
 }}
 await journalAuthor(inherited)
 // Persist results and value facts only; handles/cells/scopes are never encoded.
 await make('registryFinal',{entry:E},F,
  some({kind:'listItemAt',list:{...wait(f(p('entry'),'Entry','handle')),limit:{kind:'optionSome',value:integer(0)}},index:integer(0)},item=>
   some(item,value=>({kind:'matchResult',input:value,okBody:pat(),errBody:construct('Final',{status:text('failed'),commit:text(''),checks:text('')})}),construct('Final',{status:text('running'),commit:text(''),checks:text('')})),construct('Final',{status:text('running'),commit:text(''),checks:text('')})),[])
 await make('registryEntryEncode',{host:cap('Host'),item:E},string,
  letIn(direct('candidate',{entry:p('item')}),candidate=>letIn(direct('registryFinal',{entry:p('item')}),final=>
   some(io('storageRead',{key:memberKey(f(p('item'),'Entry','id'))}),stored=>
    letIn(parse(stored),admission=>concat(text('{'),...valueFields.flatMap((key,index)=>[text((index?',':'')+JSON.stringify(key)+':'),quote(({id:num(f(p('item'),'Entry','id')),workspace:f(p('item'),'Entry','workspace'),base:f(p('item'),'Entry','base'),previous:num(f(p('item'),'Entry','previous')),discarded:yes(f(p('item'),'Entry','discarded')),superseded:yes(f(p('item'),'Entry','superseded')),status:f(final,'Final','status'),commit:f(final,'Final','commit'),checks:f(final,'Final','checks'),settled:yes(f(candidate,'Candidate','settled')),integrated:f(p('item'),'Entry','integrated'),admission:get(admission,'identity'),fingerprint:get(admission,'fingerprint')})[key])]),text('}'))),text('null')))))
 await make('registryEntryFold',{host:cap('Host'),acc:string,item:E},string,
  concat(p('acc'),cond(eqStr(p('acc'),text('')),text(''),text(',')),direct('registryEntryEncode',{host:p('host'),item:p('item')})))
 await make('registryEntries',{host:cap('Host'),state:S},string,fold(f(p('state'),'State','entries'),text(''),'registryEntryFold',{host:p('host')},true))
 // Strict bounded parser plus identity/eligibility checks fail closed on damage.
 await make('registrySameId',{id:string,item:J},bool,eqStr(get(p('item'),'id'),p('id')),[])
 await make('registryEntryFieldsValid',{item:J},bool,all([kind(p('item'),'object'),all(valueFields.map(key=>sf(p('item'),key)))]),[])
 await make('registryEntryValid',{item:J,next:int,entries:list(J)},bool,
  all([direct('registryEntryFieldsValid',{item:p('item')}),
   lt(integer(0),integerOf(get(p('item'),'id'))),lt(integerOf(get(p('item'),'id')),p('next')),
   eqStr(num(integerOf(get(p('item'),'id'))),get(p('item'),'id')),
   not(lt(integerOf(get(p('item'),'previous')),integer(0))),lt(integerOf(get(p('item'),'previous')),integerOf(get(p('item'),'id'))),
   eqStr(num(integerOf(get(p('item'),'previous'))),get(p('item'),'previous')),
   ...['discarded','superseded','settled'].map(key=>oneOf(get(p('item'),key),['true','false'])),
   oneOf(get(p('item'),'status'),['completed','failed','stopped','timed_out','running']),
   or(eqStr(get(p('item'),'settled'),text('true')),and(eqStr(get(p('item'),'status'),text('running')),eqStr(get(p('item'),'integrated'),text('')))),
   not(eqStr(get(p('item'),'admission'),text(''))),not(eqStr(get(p('item'),'fingerprint'),text(''))),
   eqInt({kind:'listLength',value:{kind:'listFilter',source:p('entries'),function:'registrySameId',context:ctx({id:get(p('item'),'id')})}},integer(1))]),[])
 await make('registryValidateFold',{acc:bool,item:J,next:int,entries:list(J)},bool,and(p('acc'),direct('registryEntryValid',{item:p('item'),next:p('next'),entries:p('entries')})),[])
 await make('registryValid',{value:J},bool,
  letIn(integerOf(get(p('value'),'next')),next=>letIn(f(json(p('value'),'entries'),'WireJson','items'),entries=>all([
   kind(p('value'),'object'),all(['version','phase','id','token','fingerprint','intent','outcomeSource','outcome','next'].map(key=>sf(p('value'),key))),
   eqStr(get(p('value'),'version'),text('1')),oneOf(get(p('value'),'phase'),['idle','pending','ready']),
   cond(eqStr(get(p('value'),'phase'),text('idle')),all(['id','token','fingerprint','intent'].map(key=>eqStr(get(p('value'),key),text('')))),all([
    {kind:'stringStartsWith',value:get(p('value'),'id'),prefix:text('journal/v1/')},
    {kind:'stringStartsWith',value:get(p('value'),'token'),prefix:text('journal:')},not(eqStr(get(p('value'),'fingerprint'),text('')))])),
   lt(integer(0),next),eqStr(num(next),get(p('value'),'next')),kind(json(p('value'),'entries'),'array'),
   eqInt({kind:'listLength',value:entries},{kind:'int64WrappingSubtract',left:next,right:integer(1)}),
   fold(entries,boolean(true),'registryValidateFold',{next,entries}),
   cond(eqStr(get(p('value'),'phase'),text('ready')),and(eqStr(get(p('value'),'outcomeSource'),text('actual')),or({kind:'stringStartsWith',value:get(p('value'),'outcome'),prefix:text('ok:')},{kind:'stringStartsWith',value:get(p('value'),'outcome'),prefix:text('err:')})),and(eqStr(get(p('value'),'outcomeSource'),text('')),eqStr(get(p('value'),'outcome'),text(''))))]))),[])
 // New workers return retained value results in the new owner scope. Interrupted
 // Members settle as stopped; they never restart providers or become completed.
 await make('registryRestoredMember',{final:F,item:unit},result(F),ok(p('final')),[])
 await make('registryRestoreEntry',{scope,acc:list(E),item:J},list(E),
  letIn(construct('Final',{status:cond(eqStr(get(p('item'),'settled'),text('true')),get(p('item'),'status'),text('stopped')),commit:get(p('item'),'commit'),checks:get(p('item'),'checks')}),final=>
   letIn(createCell(string),phase=>letIn(createCell(string),commitSignal=>
    letIn(complete(phase,f(final,'Final','status')),()=>letIn(complete(commitSignal,f(final,'Final','commit')),()=>
     letIn({kind:'taskStart',scope:p('scope'),worker:'registryRestoredMember',item:unitValue,context:ctx({final})},handle=>
      letIn(wait(handle),()=>({kind:'listAppendItem',list:p('acc'),item:construct('Entry',{id:integerOf(get(p('item'),'id')),workspace:get(p('item'),'workspace'),base:get(p('item'),'base'),previous:integerOf(get(p('item'),'previous')),discarded:eqStr(get(p('item'),'discarded'),text('true')),superseded:eqStr(get(p('item'),'superseded'),text('true')),integrated:get(p('item'),'integrated'),phase,commitSignal,handle})})))))))) ,[])
 const load={host:cap('Host'),scope,closingSignal:oneShot(unit)}
 const initial=construct('State',{entries:L(E),next:integer(1),closing:boolean(false),closingSignal:p('closingSignal')})
 // A predicted number is an attempted association, never proof of acceptance.
 // Both the Member fact and inverse admission index must authenticate the
 // identity/fingerprint retained in the snapshot before reconstruction.
 await make('registryAssociationMatches',{stored:J,member:int,identity:string,fingerprint:string},bool,
  all([kind(p('stored'),'object'),...['identity','fingerprint','member'].map(key=>sf(p('stored'),key)),eqStr(get(p('stored'),'identity'),p('identity')),eqStr(get(p('stored'),'fingerprint'),p('fingerprint')),eqStr(get(p('stored'),'member'),num(p('member')))]),[])
 const association={host:cap('Host'),member:int,identity:string,fingerprint:string}
 const associationArgs=Object.fromEntries(Object.keys(association).filter(k=>k!=='host').map(k=>[k,p(k)]))
 await make('registryMemberAdmissionMatches',association,bool,
  some(io('storageRead',{key:memberKey(p('member'))}),stored=>direct('registryAssociationMatches',{...associationArgs,stored:parse(stored)}),boolean(false)))
 await make('registryMemberFactsMatch',{host:cap('Host'),item:J},bool,
  letIn(direct('registryMemberAdmissionMatches',{host:p('host'),member:integerOf(get(p('item'),'id')),identity:get(p('item'),'admission'),fingerprint:get(p('item'),'fingerprint')}),memberMatches=>
   some(io('storageRead',{key:admissionKey(get(p('item'),'admission'))}),stored=>and(memberMatches,direct('registryAssociationMatches',{member:integerOf(get(p('item'),'id')),identity:get(p('item'),'admission'),fingerprint:get(p('item'),'fingerprint'),stored:parse(stored)})),boolean(false))))
 await make('registryMemberFactsFold',{host:cap('Host'),acc:bool,item:J},bool,
  letIn(direct('registryMemberFactsMatch',{host:p('host'),item:p('item')}),matches=>and(p('acc'),matches)))
 await make('registryJournalFieldsValid',{stored:J},bool,
  all([kind(p('stored'),'object'),...['version','id','fingerprint','intent','effectIdentity','state','outcomeSource','outcome'].map(key=>sf(p('stored'),key))]),[])
 await make('registryJournalMatches',{stored:J,value:J},bool,
  all([direct('registryJournalFieldsValid',{stored:p('stored')}),eqStr(get(p('stored'),'version'),text('1')),
   ...['id','fingerprint','intent'].map(key=>eqStr(get(p('stored'),key),get(p('value'),key))),eqStr(get(p('stored'),'effectIdentity'),get(p('value'),'token')),
   cond(eqStr(get(p('stored'),'state'),text('pending')),and(eqStr(get(p('stored'),'outcomeSource'),text('')),eqStr(get(p('stored'),'outcome'),text(''))),
    all([eqStr(get(p('stored'),'state'),text('done')),eqStr(get(p('stored'),'outcomeSource'),text('actual')),eqStr(get(p('stored'),'outcome'),get(p('value'),'outcome'))]))]),[])
 await make('registryOutcomeMatches',{host:cap('Host'),value:J,prefix:string},bool,
  some(io('storageRead',{key:concat(p('prefix'),get(p('value'),'token'))}),stored=>eqStr(stored,get(p('value'),'outcome')),boolean(true)))
 await make('registryActualMatches',{host:cap('Host'),value:J},bool,
  some(io('storageRead',{key:get(p('value'),'id')}),stored=>letIn(direct('registryJournalMatches',{stored:parse(stored),value:p('value')}),journalMatches=>
   letIn(direct('registryOutcomeMatches',{host:p('host'),value:p('value'),prefix:text('outcome/')}),outcomeMatches=>
    letIn(direct('registryOutcomeMatches',{host:p('host'),value:p('value'),prefix:text('outcome-receipt/')}),receiptMatches=>all([journalMatches,outcomeMatches,receiptMatches])))),boolean(false)))
 await make('registryRetainedMatches',{host:cap('Host'),value:J},bool,
  letIn(fold(f(json(p('value'),'entries'),'WireJson','items'),boolean(true),'registryMemberFactsFold',{host:p('host')},true),membersMatch=>
   letIn(cond(eqStr(get(p('value'),'phase'),text('ready')),direct('registryActualMatches',{host:p('host'),value:p('value')}),boolean(true)),actualMatches=>and(membersMatch,actualMatches))))
 await make('registrySeedReceipt',{host:cap('Host'),value:J,item:unit},result(unit),
  cond(eqStr(get(p('value'),'phase'),text('ready')),
   some(io('storageRead',{key:concat(text('outcome-receipt/'),get(p('value'),'token'))}),stored=>cond(eqStr(stored,get(p('value'),'outcome')),ok(unitValue),err(text('unresolved'),unit)),
    letIn(io('storageWrite',{key:concat(text('outcome-receipt/'),get(p('value'),'token')),value:get(p('value'),'outcome')}),()=>ok(unitValue))),ok(unitValue)))
 await make('registryRestoreState',{...load,value:J},result(S),
  cond(direct('registryRetainedMatches',{host:p('host'),value:p('value')}),
   {kind:'matchResult',input:{kind:'cancellationShield',body:'registrySeedReceipt',context:ctx({host:p('host'),value:p('value')})},okBody:
    letIn(fold(f(json(p('value'),'entries'),'WireJson','items'),L(E),'registryRestoreEntry',{scope:p('scope')},true),entries=>ok(construct('State',{entries,next:integerOf(get(p('value'),'next')),closing:boolean(false),closingSignal:p('closingSignal')}))),errBody:err(pat('error'),S)},err(text('unresolved'),S)))
 await make('registryLoad',load,result(S),some(read(),stored=>letIn(parse(stored),value=>
  cond(direct('registryValid',{value}),direct('registryRestoreState',{...Object.fromEntries(Object.keys(load).map(k=>[k,p(k)])),value}),err(text('unresolved'),S))),cond(io('storageEmpty',{}),ok(initial),err(text('unresolved'),S))))
 const scoped={host:cap('Host'),transport:cap('Transport'),scope,closingSignal:oneShot(unit),wrongBoundary:bool}
 await make('registryRunState',{...scoped,state:S},result(string),
  letIn(createCell(S),last=>letIn(complete(last,p('state')),()=>
   letIn(direct('ownedTransport',{host:p('host'),transport:p('transport'),scope:p('scope'),owner:construct('AdmissionOwner',{initial:p('state'),last,tickets:L(K)}),wrongBoundary:p('wrongBoundary')}),owner=>direct('sessionFinish',{host:p('host'),scope:p('scope'),owner,wrongBoundary:p('wrongBoundary')})))))
 await s.publish(await s.call('replace',{function:'sessionScope',replacement:resolveFrames(
  {kind:'matchResult',input:cond(boolean(restorationDisabled),ok(initial),direct('registryLoad',Object.fromEntries(Object.keys(load).map(k=>[k,p(k)])))),okBody:direct('registryRunState',{...Object.fromEntries(Object.keys(scoped).map(k=>[k,p(k)])),state:pat()}),errBody:err(pat('error'))})}))
 // Atomic complete snapshot is itself actual result evidence. It precedes the
 // legacy scalar receipt, so its crash gap can seed that receipt on restart.
 const accept={host:cap('Host'),state:S,target:int,label:string,outcome:named('Outcome'),item:unit}
 await make('registryPublish',{...accept,nextState:S},result(S),some(read(),stored=>letIn(parse(stored),admission=>
  cond(and(direct('registryValid',{value:admission}),eqStr(get(admission,'token'),p('label'))),
   letIn(envelope('ready',p('nextState'),admission,rendered(p('outcome'))),value=>cond(direct('registryValid',{value:parse(value)}),letIn(io('storageWrite',{key:registryKey,value}),()=>direct('registryAcceptOriginal',Object.fromEntries(Object.keys(accept).map(k=>[k,p(k)])))),err(text('unresolved'),S))),err(text('unresolved'),S))),err(text('unresolved'),S)))
 await s.publish(await s.call('replace',{function:'journalAcceptProtected',replacement:resolveFrames(
  cond(boolean(restorationDisabled),direct('registryAcceptOriginal',Object.fromEntries(Object.keys(accept).map(k=>[k,p(k)]))),
   direct('registryPublish',{...Object.fromEntries(Object.keys(accept).map(k=>[k,p(k)])),nextState:update(p('state'),'State',{entries:{kind:'listMap',source:f(p('state'),'State','entries'),function:'ledgerEntry',context:ctx({target:p('target'),head:f(f(p('outcome'),'Outcome','candidate'),'Candidate','integrated')})}})}))) }))
 const wire={host:cap('Host'),scope,acc:W,item:string,wrongBoundary:bool},wireArgs=Object.fromEntries(Object.keys(wire).map(k=>[k,p(k)]))
 const store={host:cap('Host'),value:string,previous:string,owner:W}
 await make('registryStoreProtected',{...store,item:unit},result(W),
  ok(cond(and(direct('registryValid',{value:parse(p('value'))}),not(eqStr(p('value'),p('previous')))),letIn(io('storageWrite',{key:registryKey,value:p('value')}),()=>p('owner')),p('owner'))))
 await make('registryStoreChanged',store,W,{kind:'matchResult',input:{kind:'cancellationShield',body:'registryStoreProtected',context:ctx(Object.fromEntries(Object.keys(store).map(k=>[k,p(k)])))},okBody:pat(),errBody:p('owner')})
 await make('registryObserve',{host:cap('Host'),owner:W},W,letIn(direct('admissionCurrent',{owner:p('owner')}),state=>some(read(),stored=>letIn(parse(stored),admission=>
  cond(eqStr(get(admission,'phase'),text('pending')),p('owner'),letIn(cond(eqStr(get(admission,'phase'),text('ready')),envelope('ready',state,admission,get(admission,'outcome')),envelope('idle',state,parse(text('{}')),text(''))),value=>direct('registryStoreChanged',{host:p('host'),owner:p('owner'),previous:stored,value})))),
  letIn(envelope('idle',state,parse(text('{}')),text('')),value=>direct('registryStoreChanged',{host:p('host'),owner:p('owner'),previous:text(''),value})))))
 const route={...wire,decoded:D,identity:string,fingerprint:string}
 const d=key=>f(p('decoded'),'WireDecoded',key),q=key=>f(d('request'),'Request',key)
 const reply=value=>letIn(direct('journalReply',{host:p('host'),decoded:p('decoded'),outcome:value}),()=>p('acc'))
 const proceed=direct('journalWire',wireArgs),original=direct('wireFoldOriginal',wireArgs)
 const intent=direct('journalIntent',{acc:p('acc'),decoded:p('decoded')})
 const token=concat(text('journal:'),p('identity'),text(':'),p('fingerprint')),recordKey=concat(text('journal/v1/'),p('identity'))
 const pending=intent=>concat(text('{"id":'),quote(recordKey),text(',"token":'),quote(token),text(',"fingerprint":'),quote(p('fingerprint')),text(',"intent":'),quote(intent),text('}'))
 await make('registryCheckpoint',route,W,letIn(direct('admissionCurrent',{owner:p('acc')}),state=>some(direct('get',{state,target:q('target')}),()=>letIn(intent,intentValue=>letIn(parse(pending(intentValue)),admission=>
  letIn(envelope('pending',state,admission,text('')),value=>cond(direct('registryValid',{value:parse(value)}),letIn(io('storageWrite',{key:registryKey,value}),()=>proceed),reply(text('err:unresolved')))))),proceed)))
 await make('registryIntegrationStored',{...route,value:J},W,
  cond(eqStr(get(p('value'),'phase'),text('pending')),
   cond(eqStr(get(p('value'),'id'),recordKey),cond(eqStr(get(p('value'),'fingerprint'),p('fingerprint')),
    some(io('storageRead',{key:recordKey}),()=>proceed,
     letIn(parse(io('effectEvidence',{identity:get(p('value'),'token')})),evidence=>cond(and(eqStr(get(evidence,'state'),text('absent')),eqStr(get(p('value'),'intent'),intent)),proceed,reply(text('err:unresolved'))))),reply(text('err:payload-conflict'))),reply(text('err:unresolved'))),
   some(io('storageRead',{key:recordKey}),()=>proceed,direct('registryCheckpoint',Object.fromEntries(Object.keys(route).map(k=>[k,p(k)]))))))
 await make('registryIntegration',route,W,some(read(),stored=>direct('registryIntegrationStored',{...Object.fromEntries(Object.keys(route).map(k=>[k,p(k)])),value:parse(stored)}),
  some(io('storageRead',{key:recordKey}),()=>reply(text('err:unresolved')),direct('registryCheckpoint',Object.fromEntries(Object.keys(route).map(k=>[k,p(k)]))))))
 // Stable Member admission identity prevents a replayed scripted prefix from
 // spawning over restored state. Distinct admissions still use State.next.
 await make('registrySpawnAwait',{owner:W,decoded:D},W,
  cond(eqStr(f(f(p('decoded'),'WireDecoded','request'),'Request','op'),text('begin')),
   some(direct('admissionGet',{owner:p('owner'),target:f(f(p('decoded'),'WireDecoded','request'),'Request','target')}),ticket=>direct('admissionAwaitTicket',{owner:p('owner'),ticket}),p('owner')),p('owner')),[])
 const admission=state=>concat(text('{"identity":'),quote(p('identity')),text(',"fingerprint":'),quote(p('fingerprint')),text(',"member":'),quote(num(f(state,'State','next'))),text('}'))
 await make('registrySpawnNew',route,W,
  letIn(direct('admissionCurrent',{owner:p('acc')}),state=>letIn(admission(state),value=>letIn(io('storageWrite',{key:admissionKey(p('identity')),value}),()=>letIn(io('storageWrite',{key:memberKey(f(state,'State','next')),value}),()=>letIn(original,owner=>direct('registrySpawnAwait',{owner,decoded:p('decoded')})))))))
 await make('registrySpawn',route,W,some(io('storageRead',{key:admissionKey(p('identity'))}),stored=>letIn(parse(stored),value=>
  cond(all([kind(value,'object'),...['identity','fingerprint','member'].map(key=>sf(value,key)),eqStr(get(value,'identity'),p('identity'))]),
   cond(eqStr(get(value,'fingerprint'),p('fingerprint')),some(direct('get',{state:direct('admissionCurrent',{owner:p('acc')}),target:integerOf(get(value,'member'))}),()=>
    cond(direct('registryMemberAdmissionMatches',{host:p('host'),member:integerOf(get(value,'member')),identity:p('identity'),fingerprint:p('fingerprint')}),letIn(direct('wireWrite',{host:p('host'),decoded:p('decoded')}),()=>p('acc')),reply(text('err:unresolved'))),reply(text('err:unresolved'))),reply(text('err:payload-conflict'))),reply(text('err:unresolved')))),
  some(read(),stored=>cond(eqStr(get(parse(stored),'phase'),text('pending')),reply(text('err:unresolved')),direct('registrySpawnNew',Object.fromEntries(Object.keys(route).map(k=>[k,p(k)])))),direct('registrySpawnNew',Object.fromEntries(Object.keys(route).map(k=>[k,p(k)]))))))
 await make('registryRoute',{...wire,decoded:D},W,
  cond(or(d('notification'),eqStr(d('id'),text('null'))),cond(d('notification'),p('acc'),reply(text('err:stable-synchronous-id-required'))),
   letIn(io('digest',{value:d('id')}),identity=>letIn(io('digest',{value:p('item')}),fingerprint=>
    direct('registryIntegration',{...wireArgs,decoded:p('decoded'),identity,fingerprint})))))
 await make('registrySpawnRoute',{...wire,decoded:D},W,
  cond(or(d('notification'),eqStr(d('id'),text('null'))),cond(d('notification'),p('acc'),reply(text('err:stable-synchronous-id-required'))),
   letIn(io('digest',{value:d('id')}),identity=>letIn(io('digest',{value:p('item')}),fingerprint=>direct('registrySpawn',{...wireArgs,decoded:p('decoded'),identity,fingerprint})))))
 await make('registryWire',wire,W,letIn(direct('wireDecode',{source:p('item')}),decoded=>
  cond(eqInt(f(decoded,'WireDecoded','code'),integer(0)),
   cond(eqStr(f(f(decoded,'WireDecoded','request'),'Request','op'),text('integrate')),direct('registryRoute',{...wireArgs,decoded}),
    cond(or(eqStr(f(f(decoded,'WireDecoded','request'),'Request','op'),text('spawn')),and(eqStr(f(f(decoded,'WireDecoded','request'),'Request','op'),text('begin')),eqStr(f(f(decoded,'WireDecoded','request'),'Request','mode'),text('spawn')))),direct('registrySpawnRoute',{...wireArgs,decoded}),
     cond(or(eqStr(f(f(decoded,'WireDecoded','request'),'Request','op'),text('discard')),eqStr(f(f(decoded,'WireDecoded','request'),'Request','op'),text('begin'))),some(read(),stored=>cond(eqStr(get(parse(stored),'phase'),text('pending')),letIn(direct('journalReply',{host:p('host'),decoded,outcome:text('err:unresolved')}),()=>p('acc')),proceed),proceed),proceed))),proceed)))
 await make('registryFold',wire,W,letIn(direct('registryWire',wireArgs),owner=>direct('registryObserve',{host:p('host'),owner})))
 await s.publish(await s.call('replace',{function:'wireFold',replacement:cond(boolean(restorationDisabled),proceed,direct('registryFold',wireArgs))}))
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,declarations,nodes:s.nodes}
}
