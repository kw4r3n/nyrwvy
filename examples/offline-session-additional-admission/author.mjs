// A finite successor application policy; no new runtime primitive.
import {author as predecessorAuthor,maxFrameBytes} from '../offline-session-epoch-disposition/author.mjs'
import {string,int,bool,unit,unitValue,option,result,ok,err,cap,scope,named,oneShot,list,fold,L,p,field,direct,invoke,text,eqStr,eqInt,integer,boolean,cond,concat as cat,letIn,some,resolveFrames,and,or,not,ctx,slice} from '../offline-leader-jsonrpc/recipes.mjs'
export {maxFrameBytes}
const J=named('WireJson'),W=named('AdmissionOwner'),D=named('WireDecoded'),f=(v,k)=>field(v,'WireJson',k),io=(op,a={})=>invoke(p('host'),'Host',op,a),parse=source=>direct('dispositionParse',{source}),json=(v,k)=>direct('wireGet',{value:v,key:text(k)}),get=(v,k)=>f(json(v,k),'scalar'),kind=(v,k)=>eqStr(f(v,'kind'),text(k)),sf=(v,k)=>kind(json(v,k),'string'),quote=value=>direct('wireQuote',{value}),dig=value=>io('digest',{value}),read=key=>io('storageRead',{key:typeof key==='string'?text(key):key}),args=ps=>Object.fromEntries(Object.keys(ps).map(k=>[k,p(k)]))
const balanced=(a,fn)=>a.length===1?a[0]:fn(balanced(a.slice(0,a.length>>1),fn),balanced(a.slice(a.length>>1),fn)),all=a=>balanced(a,and),concat=(...a)=>balanced(a,cat)
const keys=['additional/v1/intent','additional/v1/blocked','additional/v1/result','additional-registry/v1','additional/v1/activation']
const remapKey=x=>x.replaceAll('epoch-registry/v1','additional-registry/v1').replaceAll('epoch-journal/v1/','additional-journal/v1/').replaceAll('epoch-lineage/v1/','additional-lineage/v1/').replaceAll('epoch/v1/','additional/v1/').replaceAll('registry-member/v1/','additional-member/v1/').replaceAll('registry-admission/v1/','additional-admission/v1/')
export async function author(s){
 const events=[],names=new Set()
 const captured={...s,create:async(n,ps,t,b,e)=>{events.push({n,ps,t,b,e});names.add(n);return s.create(n,ps,t,b,e)},call:async(n,a)=>{
  if(n==='request'&&a.op==='simulateCreateCapabilityByName'&&a.arguments.displayName==='Host')a={...a,arguments:{...a.arguments,operations:[...a.arguments.operations,{displayName:'additionalFacts',parameters:[],resultType:string,effect:'Io'}]}}
  if(n==='replace'){events.push({n:a.function,b:a.replacement,replace:true})}
  return s.call(n,a)
 }}
 await predecessorAuthor(captured)
 const make=(n,ps,t,b,e)=>s.create(n,ps,t,resolveFrames(b),e),makeAll=async(n,ps,checks)=>{const parts=[];for(let i=0;i<checks.length;i+=6){const part=n+'Part'+i;await make(part,ps,bool,all(checks.slice(i,i+6)));parts.push(direct(part,args(ps)))}await make(n,ps,bool,all(parts))}
 // Replay only the transitive runtime recipe closure, preserving original
 // declaration/replacement order and the same product/owned-handle types.
 // This is graph authoring, never generated-source editing or a host controller.
 const needed=new Set(['epochSessionOriginal','registryFold','spawn']),refs=x=>{if(Array.isArray(x))return x.flatMap(refs);if(!x||typeof x!=='object')return [];return Object.entries(x).flatMap(([k,v])=>typeof v==='string'&&['function','worker','body'].includes(k)&&names.has(v)?[v]:refs(v))}
 let changed=true;while(changed){changed=false;for(const e of events)if(needed.has(e.n)&&e.n!=='wireFold')for(const n of refs(e.b))if(!needed.has(n)){needed.add(n);changed=true}}
 // Share pure/value and unaffected lifecycle recipes. Clone only functions
 // whose storage namespace changes, then their transitive callers. This keeps
 // restoration handles and parser code on existing reviewed primitives.
 const changesKey=x=>Array.isArray(x)?x.some(changesKey):x&&typeof x==='object'?(x.kind==='stringLiteral'&&remapKey(x.value)!==x.value)||Object.values(x).some(changesKey):false
 const altered=new Set(['wireFold','epochRead','epochWrite','spawn']);for(const e of events)if(changesKey(e.b))altered.add(e.n)
 changed=true;while(changed){changed=false;for(const e of events)if(needed.has(e.n)&&refs(e.b).some(n=>altered.has(n))&&!altered.has(e.n)){altered.add(e.n);changed=true}}
 for(const n of needed)if(!altered.has(n))needed.delete(n)
 const rename=x=>Array.isArray(x)?x.map(rename):x&&typeof x==='object'?Object.fromEntries(Object.entries(x).map(([k,v])=>[k,k==='value'&&x.kind==='stringLiteral'?remapKey(v):typeof v==='string'&&['function','worker','body'].includes(k)&&needed.has(v)?'additionalRuntime_'+v:rename(v)])):x
 for(const e of events)if(needed.has(e.n)){
  const b=e.n==='wireFold'?p('acc'):rename(e.b)
  if(e.replace)await s.publish(await s.call('replace',{function:'additionalRuntime_'+e.n,replacement:b}));else await s.create('additionalRuntime_'+e.n,e.ps,e.t,b,e.e)
 }
 // Retained associations are read from their frozen original namespace; only
 // new associations are written to additional-* with activation lineage.
 const key=p('key'),suffix=prefix=>slice(key,integer(prefix.length),{kind:'stringLength',value:key}),starts=prefix=>({kind:'stringStartsWith',value:key,prefix:text(prefix)})
 const fallback=cond(starts('additional-member/v1/'),read(concat(text('registry-member/v1/'),suffix('additional-member/v1/'))),cond(starts('additional-admission/v1/'),read(concat(text('registry-admission/v1/'),suffix('additional-admission/v1/'))),{kind:'optionNone',itemType:string}))
 await s.publish(await s.call('replace',{function:'additionalRuntime_epochRead',replacement:resolveFrames(some(read(key),v=>({kind:'optionSome',value:v}),fallback))}))
 // Authenticate the accepted disposition envelope against retained mechanical
 // facts. Its required authority is the accepted bytes; current labels are
 // checked separately as optional confirmations by the inspection binding.
 const acceptedEnvelope=x=>Array.isArray(x)?x.map(acceptedEnvelope):x&&typeof x==='object'?x.kind==='capabilityInvoke'&&x.operation==='fixtureRead'&&x.arguments.some(a=>a.value.kind==='stringLiteral'&&a.value.value==='epochDispositionAuthorization')?quote(get(p('decision'),'authorization')):Object.fromEntries(Object.entries(x).map(([k,v])=>[k,k==='function'&&typeof v==='string'&&v.startsWith('dispositionEnvelopeValid')?'additionalAccepted_'+v:acceptedEnvelope(v)])):x
 for(const e of events)if(e.n.startsWith('dispositionEnvelopeValid')&&!e.replace)await s.create('additionalAccepted_'+e.n,e.ps,e.t,acceptedEnvelope(e.b),e.e)
 const boot={host:cap('Host'),request:string,facts:J},facts=p('facts'),request=p('request'),q=parse(request),check={host:cap('Host'),decision:J,facts:J},decision=p('decision')
 const fields=['id','epoch','authorization','action','scope','predecessorDigest','inventoryDigest','custodyDigest','baselineStateDigest','baselineHead','repository','repositoryId','next','memberRequest','integrationRequest','memberIdentity','integrationIdentity','selectedCommit']
 await makeAll('additionalEnvelope',check,[kind(decision,'object'),...fields.map(k=>sf(decision,k)),direct('epochAscii',{value:get(decision,'id')}),direct('epochAscii',{value:get(decision,'epoch')}),not(eqStr(get(decision,'id'),get(decision,'epoch'))),not(eqStr(get(decision,'authorization'),text(''))),eqStr(get(decision,'action'),text('activate-additional-admission')),eqStr(get(decision,'scope'),text('one-additional-member-one-synchronous-integration/v1')),letIn(parse(io('fixtureRead',{key:text('additionalAuthorization')})),v=>and(kind(v,'string'),eqStr(f(v,'scalar'),get(decision,'authorization')))),...['predecessorDigest','inventoryDigest','custodyDigest','baselineStateDigest','baselineHead','repository','repositoryId','next','selectedCommit'].map(k=>eqStr(get(decision,k),get(facts,k)))])
 // Reconstruct the predecessor graph receipt from mechanically authenticated
 // frozen facts; optional labels only confirm the accepted authority bytes.
 await make('additionalPredecessor',{host:cap('Host'),facts:J},bool,letIn(parse(get(facts,'priorFacts')),prior=>letIn(get(facts,'predecessorRequest'),request=>all([
  direct('additionalAccepted_dispositionEnvelopeValid',{host:p('host'),facts:prior,decision:parse(request)}),
  ...['dispositionAuthorityValid','dispositionEpochValid','dispositionPendingValid','dispositionCounterValid'].map(fn=>direct(fn,fn==='dispositionAuthorityValid'?{host:p('host'),facts:prior,decision:parse(request)}:{host:p('host'),facts:prior})),
  eqStr(get(facts,'predecessor'),direct('dispositionReceipt',{host:p('host'),request,facts:prior})),some(read('epoch-disposition/v1/intent'),v=>eqStr(v,request),boolean(false)),some(read('epoch-disposition/v1/receipt'),v=>eqStr(v,get(facts,'predecessor')),boolean(false))
 ]))))
 await make('additionalUnused',{acc:bool,item:J,value:string},bool,and(p('acc'),not(eqStr(f(p('item'),'scalar'),p('value')))),[])
 await make('additionalUnusedWire',{host:cap('Host'),acc:bool,item:J,value:string},bool,and(p('acc'),not(eqStr(dig(field(direct('wireDecode',{source:f(p('item'),'scalar')}),'WireDecoded','id')),p('value')))))
 await make('additionalUnusedAdmission',{acc:bool,item:J,value:string},bool,and(p('acc'),not(eqStr(get(p('item'),'admission'),p('value')))),[])
 await makeAll('additionalSeparation',check,[
  ...['id','epoch'].flatMap(label=>['member','integration'].flatMap(w=>[not(eqStr(get(decision,label),get(decision,w+'Identity'))),not(eqStr(get(decision,label),get(parse(get(decision,w+'Request')),'id')))])),
  ...['id','epoch'].map(label=>fold(f(json(facts,'reservedIds'),'items'),boolean(true),'additionalUnused',{value:get(decision,label)})),fold(f(json(facts,'authorities'),'items'),boolean(true),'additionalUnused',{value:get(decision,'authorization')}),
  ...['id','epoch'].flatMap(label=>[get(decision,label),quote(get(decision,label))].map(value=>fold(f(json(parse(get(facts,'sourceRegistry')),'entries'),'items'),boolean(true),'additionalUnusedAdmission',{value:dig(value)}))),
  ...['member','integration'].flatMap(label=>[fold(f(json(parse(get(facts,'sourceRegistry')),'entries'),'items'),boolean(true),'additionalUnusedAdmission',{value:get(decision,label+'Identity')}),fold(f(json(facts,'wireFrames'),'items'),boolean(true),'additionalUnusedWire',{host:p('host'),value:get(decision,label+'Identity')},true),fold(f(json(facts,'reservedIds'),'items'),boolean(true),'additionalUnused',{value:get(parse(get(decision,label+'Request')),'id')})]),not(eqStr(get(decision,'memberIdentity'),get(decision,'integrationIdentity')))
 ])
 // The fold is pure and compares the exact opaque JSON-string value.
 await make('additionalInputs',{host:cap('Host')},bool,all(['epochRequest','epochDispositionRequest','dispositionRequest','continuationRequest','releaseRequest','interruptedContinuationRequest'].map(k=>letIn(io('fixtureRead',{key:text(k)}),raw=>or(eqStr(raw,text('')),letIn(parse(raw),v=>and(kind(v,'string'),eqStr(f(v,'scalar'),text('')))))))))
 // New Member publication never repairs an incomplete association pair.
 // The inverse is written before the Member; either partial window stays closed.
 await make('additionalAssociationsComplete',boot,bool,some(read(concat(text('additional-member/v1/'),get(facts,'next'))),member=>some(read(concat(text('additional-admission/v1/'),get(q,'memberIdentity'))),inverse=>all([eqStr(member,inverse),direct('registryAssociationMatches',{stored:parse(member),member:some({kind:'stringParseInt64',value:get(facts,'next')},v=>v,integer(-1)),identity:get(q,'memberIdentity'),fingerprint:dig(get(q,'memberRequest'))})]),boolean(false)),some(read(concat(text('additional-admission/v1/'),get(q,'memberIdentity'))),()=>boolean(false),boolean(true))))
 await make('additionalValid',boot,bool,letIn(q,decision=>all([direct('additionalInputs',{host:p('host')}),direct('additionalAssociationsComplete',args(boot)),direct('additionalPredecessor',{host:p('host'),facts}),direct('additionalEnvelope',{host:p('host'),decision,facts}),direct('additionalSeparation',{host:p('host'),decision,facts}),...['member','integration'].map(label=>direct('epochFreshRequestValid',{host:p('host'),decision,facts,label:text(label)}))])))
 await make('additionalBlocked',{facts:J},string,concat(text('{"version":"1","original":'),quote(get(facts,'originalBlocked')),text(',"continuation":'),quote(get(facts,'continuationRequest')),text(',"fresh":'),quote(get(facts,'freshRequest')),text(',"originalOutcome":"unknown","continuationOutcome":"unknown","freshOutcome":"unknown","predecessor":'),quote(get(facts,'predecessor')),text('}')),[])
 await make('additionalResult',boot,string,letIn(dig(request),intentDigest=>letIn(dig(direct('additionalBlocked',{facts})),blockedDigest=>concat(text('{"epoch":'),quote(get(q,'epoch')),text(',"intentDigest":'),quote(intentDigest),text(',"blockedDigest":'),quote(blockedDigest),text(',"result":"ok:activated-additional-admission"}')))))
 await make('additionalActivation',boot,string,letIn(dig(request),intentDigest=>letIn(dig(direct('additionalResult',args(boot))),resultDigest=>concat(intentDigest,text(':'),resultDigest))))
 const activation=direct('additionalActivation',args(boot)),lineageKey=concat(text('additional-lineage/v1/'),dig(text('additional-registry/v1'))),initial=direct('epochInitialRegistry',{value:parse(get(facts,'sourceRegistry'))})
 const agree=(key,value)=>letIn(typeof key==='string'?text(key):key,k=>letIn(value,v=>direct('epochAgree',{host:p('host'),key:k,value:v}))),put=(key,value)=>letIn(typeof key==='string'?text(key):key,k=>letIn(value,v=>direct('epochPut',{host:p('host'),key:k,value:v})))
 const publications=[[keys[0],request],[keys[1],direct('additionalBlocked',{facts})],[keys[2],direct('additionalResult',args(boot))],[keys[3],initial],[lineageKey,activation],[keys[4],activation]]
 await makeAll('additionalPublicationValid',boot,[direct('additionalValid',args(boot)),...publications.map(([k,v])=>typeof k==='string'&&k===keys[3]?some(read(keys[4]),()=>boolean(true),direct('epochAgree',{host:p('host'),key:text(k),value:v})):agree(k,v)),...publications.slice(0,-1).map(([k],i)=>or(direct('epochExists',{host:p('host'),key:typeof k==='string'?text(k):k}),all(publications.slice(i+1).map(([later])=>not(direct('epochExists',{host:p('host'),key:typeof later==='string'?text(later):later}))))))])
 let publish=ok(text('ok:activated-additional-admission'));for(const [k,v] of publications.toReversed()){const tail=publish;publish=letIn(put(k,v),()=>tail)}
 await make('additionalPublish',{...boot,item:unit},result(string),cond(direct('additionalPublicationValid',args(boot)),publish,err(text('unresolved'))))
 await makeAll('additionalFreshEntry',{host:cap('Host'),acc:bool,item:J,decision:J,facts:J},[p('acc'),cond(eqStr(get(p('item'),'id'),get(facts,'next')),all([eqStr(get(p('item'),'admission'),get(decision,'memberIdentity')),eqStr(get(p('item'),'fingerprint'),dig(get(decision,'memberRequest'))),eqStr(get(p('item'),'base'),get(decision,'baselineHead')),eqStr(get(p('item'),'workspace'),concat(text('w'),get(facts,'next'))),eqStr(get(p('item'),'previous'),text('0')),eqStr(get(p('item'),'discarded'),text('false')),eqStr(get(p('item'),'superseded'),text('false')),or(not(eqStr(get(facts,'newPlan'),text('false'))),eqStr(get(p('item'),'integrated'),text(''))),or(not(eqStr(get(p('item'),'status'),text('completed'))),eqStr(get(p('item'),'commit'),get(decision,'selectedCommit')))]),boolean(true))])
 const startupChecks=publications.filter(([k])=>k!==keys[3]).map(([k,v])=>some(read(typeof k==='string'?text(k):k),raw=>eqStr(raw,v),boolean(false)))
 await makeAll('additionalStartup',boot,[direct('additionalValid',args(boot)),...startupChecks,some(read(keys[3]),raw=>letIn(parse(raw),value=>all([
  direct('additionalRuntime_registryValid',{value}),fold(f(json(value,'entries'),'items'),boolean(true),'additionalFreshEntry',{host:p('host'),decision:q,facts},true),direct('additionalRuntime_registryRetainedMatches',{host:p('host'),value}),not(eqStr(get(value,'phase'),text('pending'))),cond(eqStr(get(facts,'newPlan'),text('true')),eqStr(get(value,'phase'),text('ready')),eqStr(get(value,'phase'),text('idle'))),
  eqStr(fold(f(json(parse(get(facts,'sourceRegistry')),'entries'),'items'),text(''),'epochEntryFold'),fold({kind:'listFilter',source:f(json(value,'entries'),'items'),function:'dispositionIsOld',context:ctx({member:get(facts,'next')})},text(''),'epochEntryFold')),
  some(read(concat(text('additional-admission/v1/'),get(q,'memberIdentity'))),()=>eqInt(some({kind:'stringParseInt64',value:get(value,'next')},v=>v,integer(-1)),{kind:'int64WrappingAdd',left:some({kind:'stringParseInt64',value:get(facts,'next')},v=>v,integer(-1)),right:integer(1)}),eqStr(get(value,'next'),get(facts,'next')))
 ])),boolean(false))])
 const wire={host:cap('Host'),scope,acc:W,item:string,wrongBoundary:bool},route={...wire,decision:J,decoded:D,facts:J},decoded=p('decoded'),op=field(field(decoded,'WireDecoded','request'),'Request','op'),identity=p('identity'),reply=outcome=>letIn(direct('journalReply',{host:p('host'),decoded,outcome}),()=>p('acc'))
 await make('additionalOldRetry',{host:cap('Host'),acc:W,decoded:D,item:string,old:string},W,reply(cond(eqStr(p('item'),p('old')),text('err:unresolved'),text('err:payload-conflict'))))
 await make('additionalRoute',route,W,p('acc'))
 // Only the exact selected pair can create a Member or run an integration.
 const permitted=or(and(eqStr(op,text('begin')),eqStr(p('item'),get(decision,'memberRequest'))),and(eqStr(op,text('integrate')),eqStr(p('item'),get(decision,'integrationRequest'))))
 const oldFrames=[get(facts,'originalRequest'),get(facts,'freshRequest')]
 let routed=cond(permitted,cond(or(not(eqStr(op,text('integrate'))),direct('epochSelected',{acc:p('acc'),decoded,decision})),direct('additionalRuntime_registryFold',args(wire)),reply(text('err:unresolved'))),reply(cond(or(eqStr(identity,get(decision,'memberIdentity')),eqStr(identity,get(decision,'integrationIdentity'))),text('err:payload-conflict'),text('err:unresolved'))))
 for(const old of oldFrames.toReversed()){const tail=routed;routed=cond(eqStr(identity,dig(field(direct('wireDecode',{source:old}),'WireDecoded','id'))),direct('additionalOldRetry',{host:p('host'),acc:p('acc'),decoded,item:p('item'),old}),tail)}
 routed=cond(or(eqStr(op,text('begin')),eqStr(op,text('integrate'))),routed,cond(or(eqStr(op,text('spawn')),eqStr(op,text('discard'))),reply(text('err:unresolved')),direct('additionalRuntime_registryFold',args(wire))))
 await make('additionalRouteIdentified',{...route,identity:string},W,routed)
 await s.publish(await s.call('replace',{function:'additionalRoute',replacement:resolveFrames(letIn(dig(field(decoded,'WireDecoded','id')),identity=>direct('additionalRouteIdentified',{...args(route),identity})))}))
 await make('additionalWire',wire,W,some(read(keys[0]),request=>letIn(direct('wireDecode',{source:p('item')}),decoded=>cond(eqInt(field(decoded,'WireDecoded','code'),integer(0)),direct('additionalRoute',{...args(wire),decision:parse(request),decoded,facts:parse(io('additionalFacts'))}),direct('additionalRuntime_registryFold',args(wire)))),p('acc')))
 await s.publish(await s.call('replace',{function:'additionalRuntime_wireFold',replacement:direct('additionalWire',args(wire))}))
 const scoped={host:cap('Host'),transport:cap('Transport'),scope,closingSignal:oneShot(unit),wrongBoundary:bool}
 await s.publish(await s.call('replace',{function:'sessionScope',replacement:resolveFrames(letIn(parse(io('additionalFacts')),facts=>letIn(io('fixtureRead',{key:text('additionalRequest')}),input=>cond(not(eqStr(input,text(''))),{kind:'cancellationShield',body:'additionalPublish',context:ctx({host:p('host'),request:input,facts})},some(read(keys[0]),request=>cond(direct('additionalStartup',{host:p('host'),request,facts}),direct('additionalRuntime_epochSessionOriginal',args(scoped)),err(text('unresolved'))),err(text('unresolved')))))))}))
 const declarations=await s.call('inspect',{view:'declarations'}),artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,declarations,nodes:s.nodes}
}
