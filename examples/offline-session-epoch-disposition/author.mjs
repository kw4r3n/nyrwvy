// Application disposition policy composed through ordinary reviewed recipes.
import {author as epochAuthor,maxFrameBytes} from '../offline-session-epoch/author.mjs'
import {string,int,bool,unit,unitValue,option,result,ok,err,cap,scope,named,oneShot,list,fold,L,construct,p,field,direct,invoke,text,eqStr,eqInt,integer,boolean,cond,concat as cat,letIn,some,resolveFrames,and,or,not,ctx} from '../offline-leader-jsonrpc/recipes.mjs'
export {maxFrameBytes}
const J=named('WireJson'),f=(v,k)=>field(v,'WireJson',k),io=(op,a={})=>invoke(p('host'),'Host',op,a),parse=source=>direct('dispositionParse',{source}),json=(v,k)=>direct('wireGet',{value:v,key:text(k)}),get=(v,k)=>f(json(v,k),'scalar'),kind=(v,k)=>eqStr(f(v,'kind'),text(k)),sf=(v,k)=>kind(json(v,k),'string'),quote=value=>direct('wireQuote',{value})
const balanced=(a,fn)=>a.length===1?a[0]:fn(balanced(a.slice(0,a.length>>1),fn),balanced(a.slice(a.length>>1),fn)),all=a=>balanced(a,and),concat=(...a)=>balanced(a,cat),dig=value=>io('digest',{value}),read=key=>io('storageRead',{key:typeof key==='string'?text(key):key}),args=ps=>Object.fromEntries(Object.keys(ps).map(k=>[k,p(k)]))
const intentKey='epoch-disposition/v1/intent',receiptKey='epoch-disposition/v1/receipt'
export async function author(s){
 let sessionBody,parserBody
 const inherited={...s,call:async(name,a)=>{
  if(name==='request'&&a.op==='simulateCreateCapabilityByName'&&a.arguments.displayName==='Host')a={...a,arguments:{...a.arguments,operations:[...a.arguments.operations,{displayName:'epochDispositionFacts',parameters:[],resultType:string,effect:'Io'}]}}
  if(name==='replace'&&a.function==='sessionScope')sessionBody=a.replacement
  return s.call(name,a)
 },create:async(name,...a)=>{if(name==='wireParse')parserBody=a[2];return s.create(name,...a)}}
 await epochAuthor(inherited)
 const make=(n,ps,t,b,e)=>s.create(n,ps,t,resolveFrames(b),e)
 // The inherited 4096-character wire envelope remains unchanged. Raw offline
 // evidence uses its own graph-authored 262144-character parser entry, with the same
 // existing parser operations and duplicate-key/depth checks.
 const evidenceBound=x=>Array.isArray(x)?x.map(evidenceBound):x&&typeof x==='object'?x.kind==='int64Literal'&&x.value==='4096'?integer(256*1024):Object.fromEntries(Object.entries(x).map(([k,v])=>[k,evidenceBound(v)])):x
 await make('dispositionParseRaw',{source:string},named('WireParse'),evidenceBound(parserBody),[])
 await make('dispositionParse',{source:string},J,letIn(direct('dispositionParseRaw',{source:p('source')}),parsed=>cond(field(parsed,'WireParse','valid'),field(parsed,'WireParse','value'),construct('WireJson',{kind:text('missing'),scalar:text(''),items:L(J),keys:L(string)}))),[])
 const makeAll=async(n,ps,checks)=>{const calls=[];for(let i=0;i<checks.length;i+=8){const part=n+'Part'+(i/8);await make(part,ps,bool,all(checks.slice(i,i+8)));calls.push(direct(part,args(ps)))}await make(n,ps,bool,all(calls))}
 const scoped={host:cap('Host'),transport:cap('Transport'),scope,closingSignal:oneShot(unit),wrongBoundary:bool}
 await make('dispositionEpochSession',scoped,result(string),sessionBody)
 const ps={host:cap('Host'),request:string,facts:J},q=parse(p('request')),facts=p('facts')
 const envelope=['id','authorization','action','scope','epochRequest','activation','blockedDigest','registryDigest','journalDigest','planDigest','custodyDigest','inventoryDigest','newEffectState','currentStateDigest','repository','repositoryId','integrationRequest','integrationIdentity','integrationTypedId','effectIdentity','member','next','fingerprint','intent']
 const check={host:cap('Host'),decision:J,facts:J},decision=p('decision')
 await makeAll('dispositionEnvelopeValid',check,[
  kind(decision,'object'),...envelope.map(k=>sf(decision,k)),direct('epochAscii',{value:get(decision,'id')}),not(eqStr(get(decision,'authorization'),text(''))),
  eqStr(get(decision,'action'),text('retain-unresolved-epoch-integration')),eqStr(get(decision,'scope'),text('one-applied-owned-fresh-integration/v1')),
  letIn(parse(io('fixtureRead',{key:text('epochDispositionAuthorization')})),authority=>and(kind(authority,'string'),eqStr(f(authority,'scalar'),get(decision,'authorization')))),
  ...envelope.filter(k=>!['id','authorization','action','scope','integrationTypedId'].includes(k)).map(k=>eqStr(get(decision,k),get(facts,k))),
  eqStr(get(decision,'integrationTypedId'),field(direct('wireDecode',{source:get(facts,'integrationRequest')}),'WireDecoded','id')),
  eqStr(get(facts,'newEffectState'),text('applied')),eqStr(get(facts,'allNewSlots'),text('quiescent'))
 ])
 await make('dispositionAuthorityValid',check,bool,letIn(parse(get(facts,'epochRequest')),accepted=>all([
  ...['retentionId','continuationId','interruptionId','originalIdentity'].map(k=>not(eqStr(get(decision,'id'),get(facts,k)))),
  ...['id','epoch','memberIdentity','integrationIdentity'].map(k=>not(eqStr(get(decision,'id'),get(accepted,k)))),
  ...['originalRequest','memberRequest','integrationRequest'].map(k=>not(eqStr(get(decision,'id'),get(parse(get(accepted,k)),'id')))),
  not(eqStr(get(decision,'id'),get(facts,'effectIdentity'))),
  ...['retentionAuthorization','continuationAuthorization','interruptionAuthorization'].map(k=>and(not(eqStr(get(decision,'authorization'),get(facts,k))),not(eqStr(get(facts,k),text(''))))),
  not(eqStr(get(decision,'authorization'),get(accepted,'authorization'))),
  letIn(parse(io('fixtureRead',{key:text('epochAuthorization')})),current=>or(kind(current,'missing'),and(kind(current,'string'),or(eqStr(f(current,'scalar'),text('')),eqStr(f(current,'scalar'),get(accepted,'authorization'))))))
 ])))
 const epochFields=['id','epoch','authorization','action','scope','predecessorDigest','inventoryDigest','baselineStateDigest','baselineHead','repository','repositoryId','originalRequest','memberRequest','integrationRequest','memberIdentity','integrationIdentity','selectedCommit']
 await makeAll('dispositionAcceptedEnvelope',{decision:J},[kind(decision,'object'),...epochFields.map(k=>sf(decision,k)),direct('epochAscii',{value:get(decision,'id')}),direct('epochAscii',{value:get(decision,'epoch')}),not(eqStr(get(decision,'id'),get(decision,'epoch'))),not(eqStr(get(decision,'authorization'),text(''))),eqStr(get(decision,'action'),text('activate-renewed-admission')),eqStr(get(decision,'scope'),text('one-fresh-member-one-synchronous-integration/v1'))])
 await make('dispositionAcceptedEpochValid',{host:cap('Host'),request:string,facts:J},bool,letIn(parse(p('request')),decision=>all([direct('dispositionAcceptedEnvelope',{decision}),...['epochLineageValid','epochOriginalRequestValid'].map(fn=>direct(fn,{host:p('host'),decision,facts})),...['member','integration'].map(label=>direct('epochFreshRequestValid',{host:p('host'),decision,facts,label:text(label)})),not(eqStr(get(decision,'memberIdentity'),get(decision,'integrationIdentity'))),eqStr(get(decision,'selectedCommit'),get(facts,'selectedCommit'))])))
 // Epoch activation and blocked bytes are reconstructed by the original graph.
 // The pending current registry is never projected as ready/idle.
 await make('dispositionEpochValid',{host:cap('Host'),facts:J},bool,letIn(get(facts,'epochRequest'),request=>letIn(parse(get(facts,'epochFacts')),prior=>letIn(direct('epochBlocked',{host:p('host'),request,facts:prior}),blocked=>letIn(dig(request),intentDigest=>letIn(concat(text('{"epoch":'),quote(get(parse(request),'epoch')),text(',"intentDigest":'),quote(intentDigest),text(',"blockedDigest":'),quote(dig(blocked)),text(',"result":"ok:activated-renewed-admission"}')),resultValue=>all([
  direct('dispositionAcceptedEpochValid',{host:p('host'),request,facts:prior}),
  ...[['epoch/v1/intent',request],['epoch/v1/blocked',blocked],['epoch/v1/result',resultValue],['epoch/v1/activation',concat(intentDigest,text(':'),dig(resultValue))]].map(([key,value])=>some(read(key),stored=>eqStr(stored,value),boolean(false)))
 ])))))))
 const pendingParams={host:cap('Host'),facts:J,registry:J,journal:J},registry=p('registry'),journal=p('journal')
 await makeAll('dispositionPendingChecks',pendingParams,[
  direct('registryValid',{value:registry}),direct('registryRetainedMatches',{host:p('host'),value:registry}),eqStr(get(registry,'phase'),text('pending')),
  ...['outcome','outcomeSource'].map(k=>eqStr(get(registry,k),text(''))),eqStr(get(registry,'next'),get(facts,'next')),
  eqStr(get(registry,'id'),concat(text('epoch-journal/v1/'),get(facts,'integrationIdentity'))),eqStr(get(registry,'token'),get(facts,'effectIdentity')),
  eqStr(get(registry,'fingerprint'),dig(get(facts,'integrationRequest'))),eqStr(get(registry,'intent'),get(facts,'intent')),
  direct('registryJournalMatches',{stored:journal,value:registry}),eqStr(get(journal,'state'),text('pending')),
  ...['outcome','outcomeSource'].map(k=>eqStr(get(journal,k),text(''))),
  letIn(direct('wireDecode',{source:get(facts,'integrationRequest')}),decoded=>all([
   eqInt(field(decoded,'WireDecoded','code'),integer(0)),not(field(decoded,'WireDecoded','notification')),
   not(eqStr(field(decoded,'WireDecoded','id'),text('null'))),eqStr(dig(field(decoded,'WireDecoded','id')),get(facts,'integrationIdentity')),
   eqStr(field(field(decoded,'WireDecoded','request'),'Request','op'),text('integrate')),
   eqInt(field(field(decoded,'WireDecoded','request'),'Request','target'),some({kind:'stringParseInt64',value:get(facts,'member')},v=>v,integer(-1)))
  ])),
  ...['outcome/','outcome-receipt/'].map(prefix=>some(read(concat(text(prefix),get(facts,'effectIdentity'))),()=>boolean(false),boolean(true)))
 ])
 await make('dispositionPendingValid',{host:cap('Host'),facts:J},bool,direct('dispositionPendingChecks',{host:p('host'),facts,registry:parse(get(facts,'registry')),journal:parse(get(facts,'journal'))}))
 await make('dispositionIsOld',{item:J,member:string},bool,not(eqStr(get(p('item'),'id'),p('member'))),[])
 await make('dispositionFreshEntry',{host:cap('Host'),acc:bool,item:J,facts:J},bool,and(p('acc'),cond(eqStr(get(p('item'),'id'),get(facts,'member')),letIn(parse(get(facts,'epochRequest')),accepted=>all([eqStr(get(p('item'),'admission'),get(accepted,'memberIdentity')),eqStr(get(p('item'),'fingerprint'),dig(get(accepted,'memberRequest'))),eqStr(get(p('item'),'base'),get(accepted,'baselineHead')),eqStr(get(p('item'),'commit'),get(accepted,'selectedCommit')),eqStr(get(p('item'),'status'),text('completed')),eqStr(get(p('item'),'settled'),text('true')),eqStr(get(p('item'),'integrated'),text('')),eqStr(get(p('item'),'discarded'),text('false')),eqStr(get(p('item'),'superseded'),text('false')),eqStr(get(p('item'),'workspace'),get(parse(get(facts,'intent')),'workspace'))])),boolean(true))))
 await make('dispositionCounterValid',{host:cap('Host'),facts:J},bool,letIn(parse(get(facts,'registry')),registry=>letIn(parse(get(facts,'originalRegistry')),original=>all([
  eqStr(get(facts,'member'),get(original,'next')),
  eqInt(some({kind:'stringParseInt64',value:get(registry,'next')},v=>v,integer(-1)),{kind:'int64WrappingAdd',left:some({kind:'stringParseInt64',value:get(original,'next')},v=>v,integer(-1)),right:integer(1)}),
  eqStr(fold(f(json(original,'entries'),'items'),text(''),'epochEntryFold'),fold({kind:'listFilter',source:f(json(registry,'entries'),'items'),function:'dispositionIsOld',context:ctx({member:get(facts,'member')})},text(''),'epochEntryFold')),
  fold(f(json(registry,'entries'),'items'),boolean(true),'dispositionFreshEntry',{host:p('host'),facts},true)
 ]))))
 await make('dispositionAdminInputsValid',{host:cap('Host')},bool,all([eqStr(io('fixtureRead',{key:text('epochRequest')}),text('')),...['dispositionRequest','continuationRequest','releaseRequest','interruptedContinuationRequest'].map(key=>letIn(parse(io('fixtureRead',{key:text(key)})),input=>or(kind(input,'missing'),and(kind(input,'string'),eqStr(f(input,'scalar'),text(''))))))]))
 await make('dispositionRequestValid',ps,bool,letIn(q,decision=>all([
  direct('dispositionAdminInputsValid',{host:p('host')}),direct('dispositionEnvelopeValid',{host:p('host'),decision,facts}),direct('dispositionAuthorityValid',{host:p('host'),decision,facts}),direct('dispositionEpochValid',{host:p('host'),facts}),direct('dispositionPendingValid',{host:p('host'),facts}),direct('dispositionCounterValid',{host:p('host'),facts})
 ])))
 // Raw records remain in the receipt as well as their separate immutable files.
 const retained=['epochRequest','activation','blocked','originalRegistry','registry','journal','plan','custody','inventory','epochFacts','integrationRequest','integrationTypedId','integrationIdentity','effectIdentity','fingerprint','intent','member','next','newEffectState','currentStateDigest','custodyDigest','inventoryDigest']
 const receiptParts=[];for(let i=0;i<retained.length;i+=6){const name='dispositionReceiptFields'+i;await make(name,{facts:J},string,concat(...retained.slice(i,i+6).flatMap(k=>[text(','+JSON.stringify(k)+':'),quote(k==='integrationTypedId'?field(direct('wireDecode',{source:get(facts,'integrationRequest')}),'WireDecoded','id'):get(facts,k))])),[]);receiptParts.push(direct(name,{facts}))}
 await make('dispositionReceipt',ps,string,concat(text('{"version":"1","request":'),quote(p('request')),text(',"requestDigest":'),quote(dig(p('request'))),...receiptParts,text(',"originalOutcome":"unknown","continuationOutcome":"unknown","freshOutcome":"unknown","admission":"closed","result":"ok:retained-unresolved-epoch-integration"}')))
 const pub={...ps,item:unit}
 await make('dispositionPublish',pub,result(string),letIn(direct('dispositionReceipt',args(ps)),receipt=>cond(all([
  direct('dispositionRequestValid',args(ps)),direct('epochAgree',{host:p('host'),key:text(intentKey),value:p('request')}),direct('epochAgree',{host:p('host'),key:text(receiptKey),value:receipt}),
  or(direct('epochExists',{host:p('host'),key:text(intentKey)}),not(direct('epochExists',{host:p('host'),key:text(receiptKey)})))
 ]),letIn(direct('epochPut',{host:p('host'),key:text(intentKey),value:p('request')}),()=>letIn(direct('epochPut',{host:p('host'),key:text(receiptKey),value:receipt}),()=>ok(text('ok:retained-unresolved-epoch-integration')))),err(text('unresolved')))))
 await s.publish(await s.call('replace',{function:'sessionScope',replacement:resolveFrames(letIn(io('fixtureRead',{key:text('epochDispositionRequest')}),request=>cond(not(eqStr(request,text(''))),letIn(parse(io('epochDispositionFacts')),facts=>({kind:'cancellationShield',body:'dispositionPublish',context:ctx({host:p('host'),request,facts})})),cond(or(direct('epochExists',{host:p('host'),key:text(intentKey)}),direct('epochExists',{host:p('host'),key:text(receiptKey)})),err(text('unresolved')),direct('dispositionEpochSession',args(scoped))))))}))
 const declarations=await s.call('inspect',{view:'declarations'}),artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,declarations,nodes:s.nodes}
}
