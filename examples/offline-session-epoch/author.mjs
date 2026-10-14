// One bounded admission epoch composed through ordinary reviewed graph recipes.
import {author as registryAuthor,maxFrameBytes} from '../offline-session-registry/author.mjs'
import {string,int,bool,unit,unitValue,option,result,ok,err,pat,cap,scope,named,oneShot,list,p,field,construct,L,direct,invoke,text,eqStr,eqInt,integer,boolean,cond,concat as rawConcat,letIn,some,resolveFrames,and,or,not,ctx,fold,lt} from '../offline-leader-jsonrpc/recipes.mjs'
export {maxFrameBytes}
const S=named('State'),W=named('AdmissionOwner'),D=named('WireDecoded'),J=named('WireJson')
const f=(v,t,k)=>field(v,t,k),io=(op,args={})=>invoke(p('host'),'Host',op,args),get=(v,k)=>f(direct('wireGet',{value:v,key:typeof k==='string'?text(k):k}),'WireJson','scalar'),json=(v,k)=>direct('wireGet',{value:v,key:text(k)})
const parse=source=>direct('journalParse',{source}),kind=(v,k)=>eqStr(f(v,'WireJson','kind'),text(k)),sf=(v,k)=>kind(json(v,k),'string'),quote=value=>direct('wireQuote',{value})
const balanced=(a,fn)=>a.length===1?a[0]:fn(balanced(a.slice(0,a.length>>1),fn),balanced(a.slice(a.length>>1),fn)),all=a=>balanced(a,and),concat=(...a)=>balanced(a,rawConcat)
const args=ps=>Object.fromEntries(Object.keys(ps).map(k=>[k,p(k)])),read=k=>io('storageRead',{key:text(k)}),write=(k,value)=>io('storageWrite',{key:text(k),value}),dig=value=>io('digest',{value})
const reqFields=['id','epoch','authorization','action','scope','predecessorDigest','inventoryDigest','baselineStateDigest','baselineHead','repository','repositoryId','originalRequest','memberRequest','integrationRequest','memberIdentity','integrationIdentity','selectedCommit']
export async function author(s,{gateDisabled=false}={}){
 let sessionBody,originalValid,spawnBody,spawnParams
 const remap=x=>{
  if(Array.isArray(x))return x.map(remap)
  if(!x||typeof x!=='object')return x
  if(x.kind==='stringLiteral')return {...x,value:x.value.replaceAll('registry/v1','epoch-registry/v1').replaceAll('journal/v1/','epoch-journal/v1/')}
  const r=Object.fromEntries(Object.entries(x).map(([k,v])=>[k,remap(v)]))
  // Values retain one activation lineage sidecar per fresh application record.
  if(r.kind==='capabilityInvoke'&&['storageRead','storageWrite'].includes(r.operation))return direct(r.operation==='storageRead'?'epochRead':'epochWrite',{host:r.capabilityValue,...Object.fromEntries(r.arguments.map(a=>[a.parameter,a.value]))})
  return r
 }
 // Declare generic fixture inputs and physical value inspection, never an
 // application controller. The graph owns request and epoch policy below.
 const inherited={...s,call:async(name,a)=>{
  if(name==='request'&&a.op==='simulateCreateCapabilityByName'&&a.arguments.displayName==='Host')a={...a,arguments:{...a.arguments,operations:[...a.arguments.operations,...[['fixtureRead',{key:string},string],['physicalFacts',{},string]].map(([displayName,ps,resultType])=>({displayName,parameters:Object.entries(ps).map(([displayName,type])=>({displayName,type})),resultType,effect:'Io'}))]}}
  if(name==='replace'&&a.function==='sessionScope')sessionBody=remap(a.replacement)
  if(name==='replace')a={...a,replacement:remap(a.replacement)}
  return s.call(name,a)
 },create:async(name,ps,t,body,effects)=>s.create(name,ps,t,remap(body),effects)}
 // Forward declarations are authored before callers; registry author creates
 // Host, so storage wrappers are inserted at the first graph function.
 let wrappers=false
 const createOriginal=inherited.create
 inherited.create=async(name,...a)=>{
  if(name==='registryValid')originalValid=a[2]
  if(name==='spawn'){spawnParams=a[0];spawnBody=a[2]}
  if(!wrappers){wrappers=true
   await s.create('epochRead',{host:cap('Host'),key:string},option(string),io('storageRead',{key:p('key')}))
   await s.create('epochWrite',{host:cap('Host'),key:string,value:string},unit,io('storageWrite',{key:p('key'),value:p('value')}))
  }
  return createOriginal(name,...a)
 }
 await registryAuthor(inherited)
 const make=(n,ps,t,b,e)=>s.create(n,ps,t,resolveFrames(b),e)
 await make('epochOriginalValid',{value:J},bool,originalValid,[])
 const scoped={host:cap('Host'),transport:cap('Transport'),scope,closingSignal:oneShot(unit),wrongBoundary:bool}
 await make('epochSessionOriginal',scoped,result(string),sessionBody)
 // Immutable agreement includes absent -> exactly published and exact replay.
 await make('epochAgree',{host:cap('Host'),key:string,value:string},bool,some(io('storageRead',{key:p('key')}),stored=>eqStr(stored,p('value')),boolean(true)))
 await make('epochPut',{host:cap('Host'),key:string,value:string},unit,some(io('storageRead',{key:p('key')}),()=>unitValue,io('storageWrite',{key:p('key'),value:p('value')})))
 await make('epochAsciiChar',{acc:bool,item:string},bool,and(p('acc'),{kind:'stringContains',value:text('abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-'),substring:p('item')}),[])
 await make('epochAscii',{value:string},bool,all([lt(integer(0),{kind:'stringLength',value:p('value')}),lt({kind:'stringLength',value:p('value')},integer(81)),fold({kind:'stringCodePoints',value:p('value')},boolean(true),'epochAsciiChar')]),[])
 const decoded=v=>direct('wireDecode',{source:v}),typed=v=>f(decoded(v),'WireDecoded','id'),op=v=>f(f(decoded(v),'WireDecoded','request'),'Request','op'),target=v=>f(f(decoded(v),'WireDecoded','request'),'Request','target')
 const request=p('request'),facts=p('facts'),q=parse(request)
 const validation={host:cap('Host'),decision:J,facts:J},decision=p('decision')
 await make('epochEnvelopeValid',validation,bool,all([
   kind(decision,'object'),...reqFields.map(k=>sf(decision,k)),eqStr(get(decision,'action'),text('activate-renewed-admission')),eqStr(get(decision,'scope'),text('one-fresh-member-one-synchronous-integration/v1')),
   direct('epochAscii',{value:get(decision,'id')}),direct('epochAscii',{value:get(decision,'epoch')}),not(eqStr(get(decision,'id'),get(decision,'epoch'))),not(eqStr(get(decision,'authorization'),text(''))),
   letIn(parse(io('fixtureRead',{key:text('epochAuthorization')})),authority=>and(kind(authority,'string'),eqStr(f(authority,'WireJson','scalar'),get(decision,'authorization'))))]))
 await make('epochLineageValid',validation,bool,all([
   ...['predecessorDigest','inventoryDigest','baselineStateDigest','baselineHead','repository','repositoryId'].map(k=>eqStr(get(decision,k),get(facts,k))),eqStr(get(facts,'state'),text('applied')),
   ...['retentionId','continuationId','interruptionId','originalIdentity'].map(k=>and(not(eqStr(get(decision,'id'),get(facts,k))),not(eqStr(get(decision,'epoch'),get(facts,k))))),
   ...['retentionAuthorization','continuationAuthorization','interruptionAuthorization'].map(k=>and(not(eqStr(get(decision,'authorization'),get(facts,k))),not(eqStr(get(facts,k),text(''))))),
   some(read('registry/v1'),raw=>letIn(parse(raw),original=>and(direct('epochOriginalValid',{value:original}),direct('registryRetainedMatches',{host:p('host'),value:original}))),boolean(false))]))
 await make('epochUnusedMemberId',{acc:bool,item:J,identity:string},bool,and(p('acc'),not(eqStr(get(p('item'),'admission'),p('identity')))),[])
 await make('epochOriginalRequestValid',validation,bool,letIn(decoded(get(decision,'originalRequest')),old=>all([
   eqInt(f(old,'WireDecoded','code'),integer(0)),eqStr(f(f(old,'WireDecoded','request'),'Request','op'),text('integrate')),not(f(old,'WireDecoded','notification')),not(eqStr(f(old,'WireDecoded','id'),text('null'))),
   eqStr(dig(f(old,'WireDecoded','id')),get(facts,'originalIdentity')),eqStr(dig(get(decision,'originalRequest')),get(facts,'originalFingerprint')),
   some(read('registry/v1'),raw=>fold(f(json(parse(raw),'entries'),'WireJson','items'),boolean(true),'epochUnusedMemberId',{identity:get(decision,'memberIdentity')}),boolean(false)),
   some(io('storageRead',{key:concat(text('journal/v1/'),get(decision,'integrationIdentity'))}),()=>boolean(false),boolean(true)),
   letIn(concat(text('registry-admission/v1/'),get(decision,'memberIdentity')),key=>some(io('storageRead',{key}),()=>some(read('epoch/v1/activation'),activation=>some(io('storageRead',{key:concat(text('epoch-lineage/v1/'),dig(key))}),lineage=>eqStr(lineage,activation),boolean(false)),boolean(false)),boolean(true)))
 ])))
 await make('epochFreshRequestValid',{...validation,label:string},bool,letIn(get(decision,concat(p('label'),text('Request'))),source=>letIn(decoded(source),value=>all([
   eqInt(f(value,'WireDecoded','code'),integer(0)),not(f(value,'WireDecoded','notification')),not(eqStr(f(value,'WireDecoded','id'),text('null'))),
   eqStr(dig(f(value,'WireDecoded','id')),get(decision,concat(p('label'),text('Identity')))),not(eqStr(dig(f(value,'WireDecoded','id')),get(facts,'originalIdentity'))),
   eqInt(f(f(value,'WireDecoded','request'),'Request','target'),some({kind:'stringParseInt64',value:get(facts,'next')},v=>v,integer(-1))),
   cond(eqStr(p('label'),text('member')),and(eqStr(f(f(value,'WireDecoded','request'),'Request','op'),text('begin')),eqStr(f(f(value,'WireDecoded','request'),'Request','mode'),text('spawn'))),eqStr(f(f(value,'WireDecoded','request'),'Request','op'),text('integrate')))
 ]))))
 await make('epochRequestValid',{host:cap('Host'),request:string,facts:J},bool,
  letIn(q,decision=>all([
   ...['epochEnvelopeValid','epochLineageValid','epochOriginalRequestValid'].map(fn=>direct(fn,{host:p('host'),decision,facts})),
   ...['member','integration'].map(label=>direct('epochFreshRequestValid',{host:p('host'),decision,facts,label:text(label)})),
   not(eqStr(get(decision,'memberIdentity'),get(decision,'integrationIdentity'))),eqStr(get(decision,'selectedCommit'),get(facts,'selectedCommit'))
  ])))
 // Block entries retain raw accepted requests/intent, exact canonical typed
 // identity, fingerprint, correlations and prior receipts, without outcomes.
 await make('epochBlocked',{host:cap('Host'),request:string,facts:J},string,
  letIn(q,q=>concat(text('{"version":"1","predecessorEpoch":"original/v1","originalClass":"integration","originalTypedId":'),quote(typed(get(q,'originalRequest'))),text(',"originalIdentity":'),quote(get(facts,'originalIdentity')),text(',"originalRequest":'),quote(get(q,'originalRequest')),text(',"originalEffectIdentity":'),quote(concat(text('journal:'),get(facts,'originalIdentity'),text(':'),get(facts,'originalFingerprint'))),text(',"originalFingerprint":'),quote(get(facts,'originalFingerprint')),text(',"originalIntent":'),quote(get(facts,'originalIntent')),text(',"member":'),quote(get(facts,'member')),text(',"continuationClass":"administrative-continuation","continuationId":'),quote(get(facts,'continuationId')),text(',"continuationRequest":'),quote(get(facts,'continuationRequest')),text(',"continuationRequestDigest":'),quote(get(facts,'continuationRequestDigest')),text(',"continuationIntent":'),some(read('physical-continuation-intent/v1'),raw=>quote(raw),text('null')),text(',"continuationIntentDigest":'),quote(get(facts,'continuationIntentDigest')),text(',"custodyDigest":'),quote(get(facts,'custodyDigest')),text(',"predecessorDigest":'),quote(get(facts,'predecessorDigest')),text(',"originalOutcome":"unknown","continuationOutcome":"unknown"}'))))
 const values=['id','workspace','base','previous','discarded','superseded','status','commit','checks','settled','integrated','admission','fingerprint']
 await make('epochEntryEncode',{item:J},string,concat(text('{'),...values.flatMap((k,i)=>[text((i?',':'')+JSON.stringify(k)+':'),quote(get(p('item'),k))]),text('}')),[])
 await make('epochEntryFold',{acc:string,item:J},string,concat(p('acc'),cond(eqStr(p('acc'),text('')),text(''),text(',')),direct('epochEntryEncode',{item:p('item')})),[])
 await make('epochInitialRegistry',{value:J},string,concat(text('{"version":"1","phase":"idle","id":"","token":"","fingerprint":"","intent":"","outcomeSource":"","outcome":"","next":'),quote(get(p('value'),'next')),text(',"entries":['),fold(f(json(p('value'),'entries'),'WireJson','items'),text(''),'epochEntryFold'),text(']}')),[])
 const boot={host:cap('Host'),request:string,facts:J},valueKeys=['epoch/v1/intent','epoch/v1/blocked','epoch/v1/result','epoch/v1/activation']
 await make('epochExists',{host:cap('Host'),key:string},bool,some(io('storageRead',{key:p('key')}),()=>boolean(true),boolean(false)))
 await make('epochPublicationOrder',{host:cap('Host')},bool,all(valueKeys.slice(0,3).map((key,i)=>or(direct('epochExists',{host:p('host'),key:text(key)}),all([...valueKeys.slice(i+1),'epoch-registry/v1'].map(k=>not(direct('epochExists',{host:p('host'),key:text(k)}))))))))
 const activationParams={host:cap('Host'),activation:string}
 await make('epochActivate',activationParams,result(string),letIn(direct('epochPut',{host:p('host'),key:concat(text('epoch-lineage/v1/'),dig(text('epoch-registry/v1'))),value:p('activation')}),()=>letIn(direct('epochPut',{host:p('host'),key:text(valueKeys[3]),value:p('activation')}),()=>ok(text('ok:activated-renewed-admission')))))
 await make('epochRegistryAgree',{host:cap('Host')},bool,some(read(valueKeys[3]),()=>boolean(true),some(read('epoch-registry/v1'),stored=>some(read('registry/v1'),raw=>eqStr(stored,direct('epochInitialRegistry',{value:parse(raw)})),boolean(false)),boolean(true))))
 await make('epochSeedRegistry',activationParams,result(string),some(read('epoch-registry/v1'),()=>direct('epochActivate',args(activationParams)),some(read('registry/v1'),raw=>letIn(write('epoch-registry/v1',direct('epochInitialRegistry',{value:parse(raw)})),()=>direct('epochActivate',args(activationParams))),err(text('unresolved')))))
 const publishParams={...boot,blocked:string,resultValue:string,activation:string}
 await make('epochPublishValidated',publishParams,result(string),cond(all([direct('epochRequestValid',args(boot)),direct('epochPublicationOrder',{host:p('host')}),direct('epochRegistryAgree',{host:p('host')}),...[[valueKeys[0],request],[valueKeys[1],p('blocked')],[valueKeys[2],p('resultValue')],[valueKeys[3],p('activation')],[concat(text('epoch-lineage/v1/'),dig(text('epoch-registry/v1'))),p('activation')]].map(([key,value])=>direct('epochAgree',{host:p('host'),key:typeof key==='string'?text(key):key,value}))]),
  letIn(direct('epochPut',{host:p('host'),key:text(valueKeys[0]),value:request}),()=>letIn(direct('epochPut',{host:p('host'),key:text(valueKeys[1]),value:p('blocked')}),()=>letIn(direct('epochPut',{host:p('host'),key:text(valueKeys[2]),value:p('resultValue')}),()=>direct('epochSeedRegistry',args(activationParams))))),err(text('unresolved'))))
 await make('epochPublication',boot,result(string),letIn(direct('epochBlocked',args(boot)),blocked=>letIn(dig(request),intentDigest=>letIn(dig(blocked),blockedDigest=>letIn(concat(text('{"epoch":'),quote(get(q,'epoch')),text(',"intentDigest":'),quote(intentDigest),text(',"blockedDigest":'),quote(blockedDigest),text(',"result":"ok:activated-renewed-admission"}')),resultValue=>direct('epochPublishValidated',{...args(boot),blocked,resultValue,activation:concat(intentDigest,text(':'),dig(resultValue))}))))))
 // Normal startup never finishes administrative windows. It authenticates all
 // publications before restoration, input, Member admission or effects.
 await make('epochCurrentComplete',{host:cap('Host'),request:string,facts:J},bool,
  some(read('epoch-registry/v1'),raw=>letIn(parse(raw),value=>all([
   direct('registryValid',{value}),not(eqStr(get(value,'phase'),text('pending'))),
   or(eqStr(get(facts,'newPlan'),text('false')),eqStr(get(value,'phase'),text('ready'))),
   some(io('storageRead',{key:concat(text('registry-admission/v1/'),get(parse(request),'memberIdentity'))}),()=>eqInt({kind:'listLength',value:{kind:'listFilter',source:f(json(value,'entries'),'WireJson','items'),function:'registrySameId',context:ctx({id:get(facts,'next')})}},integer(1)),boolean(true))
  ])),boolean(false)))
 await make('epochStartup',{host:cap('Host'),request:string,facts:J},bool,
  letIn(direct('epochBlocked',args(boot)),blocked=>letIn(dig(request),intentDigest=>letIn(concat(text('{"epoch":'),quote(get(q,'epoch')),text(',"intentDigest":'),quote(intentDigest),text(',"blockedDigest":'),quote(dig(blocked)),text(',"result":"ok:activated-renewed-admission"}')),resultValue=>all([direct('epochRequestValid',args(boot)),direct('epochCurrentComplete',args(boot)),...[[valueKeys[0],request],[valueKeys[1],blocked],[valueKeys[2],resultValue],[valueKeys[3],concat(intentDigest,text(':'),dig(resultValue))]].map(([key,value])=>some(read(key),stored=>eqStr(stored,value),boolean(false)))])))))
 await s.publish(await s.call('replace',{function:'sessionScope',replacement:resolveFrames(letIn(parse(io('physicalFacts')),facts=>letIn(io('fixtureRead',{key:text('epochRequest')}),input=>cond(not(eqStr(input,text(''))),direct('epochPublication',{host:p('host'),request:input,facts}),some(read(valueKeys[0]),request=>cond(or(boolean(gateDisabled),direct('epochStartup',{host:p('host'),request,facts})),direct('epochSessionOriginal',args(scoped)),err(text('unresolved'))),err(text('unresolved')))))))}))
 // Every future graph-authored write/read carries the exact retained activation.
 await s.publish(await s.call('replace',{function:'epochWrite',replacement:resolveFrames(some(read(valueKeys[3]),lineage=>letIn(io('storageWrite',{key:concat(text('epoch-lineage/v1/'),dig(p('key'))),value:lineage}),()=>io('storageWrite',{key:p('key'),value:p('value')})),io('storageWrite',{key:p('key'),value:p('value')})))}))
 if(spawnBody){const baseline=x=>Array.isArray(x)?x.map(baseline):x&&typeof x==='object'?x.kind==='stringLiteral'&&x.value==='base'?some(read(valueKeys[0]),request=>get(parse(request),'baselineHead'),text('')):Object.fromEntries(Object.entries(x).map(([k,v])=>[k,baseline(v)])):x;await s.publish(await s.call('replace',{function:'spawn',replacement:resolveFrames(baseline(spawnBody))}))}
 // The one authorized Member/request may use its new namespace; retained
 // original Member observations still read their frozen associations.
 const wire={host:cap('Host'),scope,acc:W,item:string,wrongBoundary:bool}
 const routing={...wire,decision:J,decoded:D},wireDecoded=p('decoded'),operation=f(f(wireDecoded,'WireDecoded','request'),'Request','op')
 const reply=outcome=>letIn(direct('journalReply',{host:p('host'),decoded:wireDecoded,outcome}),()=>p('acc'))
 const permitted=or(and(eqStr(operation,text('integrate')),eqStr(p('item'),get(decision,'integrationRequest'))),and(eqStr(operation,text('begin')),eqStr(p('item'),get(decision,'memberRequest'))))
 await make('epochSelected',{acc:W,decoded:D,decision:J},bool,some(direct('get',{state:direct('admissionCurrent',{owner:p('acc')}),target:f(f(wireDecoded,'WireDecoded','request'),'Request','target')}),entry=>and(eqStr(f(entry,'Entry','base'),get(decision,'baselineHead')),eqStr(f(direct('candidate',{entry}),'Candidate','commit'),get(decision,'selectedCommit'))),boolean(false)),[])
 await make('epochRoute',routing,W,cond(or(eqStr(operation,text('integrate')),eqStr(operation,text('begin'))),
  letIn(dig(f(wireDecoded,'WireDecoded','id')),identity=>cond(eqStr(identity,dig(typed(get(decision,'originalRequest')))),reply(cond(eqStr(p('item'),get(decision,'originalRequest')),text('err:unresolved'),text('err:payload-conflict'))),
   cond(permitted,cond(or(not(eqStr(operation,text('integrate'))),direct('epochSelected',{acc:p('acc'),decoded:wireDecoded,decision})),direct('registryFold',args(wire)),reply(text('err:unresolved'))),reply(cond(or(eqStr(identity,get(decision,'integrationIdentity')),eqStr(identity,get(decision,'memberIdentity'))),text('err:payload-conflict'),text('err:unresolved')))))),
  cond(or(eqStr(operation,text('discard')),eqStr(operation,text('spawn'))),reply(text('err:unresolved')),direct('registryFold',args(wire)))))
 await make('epochWire',wire,W,some(read(valueKeys[0]),request=>letIn(parse(request),decision=>letIn(decoded(p('item')),decoded=>
  cond(eqInt(f(decoded,'WireDecoded','code'),integer(0)),direct('epochRoute',{...args(wire),decision,decoded}),direct('registryFold',args(wire))))),p('acc')))
 await s.publish(await s.call('replace',{function:'wireFold',replacement:direct('epochWire',args(wire))}))
 const declarations=await s.call('inspect',{view:'declarations'}),artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,declarations,nodes:s.nodes}
}
