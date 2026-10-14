// Application policy authored with ordinary reviewed graph primitives only.
import {author as readerAuthor,maxFrameBytes} from '../offline-session-reader-propagation/author.mjs'
import {string,unit,unitValue,option,result,ok,pat,cap,scope,bool,named,p,field,construct,L,direct,invoke,text,eqStr,integer,boolean,cond,concat,letIn,some,resolveFrames,and,or,not,ctx} from '../offline-leader-jsonrpc/recipes.mjs'
export {maxFrameBytes}
const W=named('AdmissionOwner'),D=named('WireDecoded'),Q=named('Request')
const f=(value,type,key)=>field(value,type,key),d=key=>f(p('decoded'),'WireDecoded',key)
const io=(operation,args)=>invoke(p('host'),'Host',operation,args)
const quote=value=>direct('wireQuote',{value})
const get=(value,key)=>f(direct('wireGet',{value,key:text(key)}),'WireJson','scalar')
const stringField=(value,key)=>eqStr(f(direct('wireGet',{value,key:text(key)}),'WireJson','kind'),text('string'))
const outcomeValid=value=>or({kind:'stringStartsWith',value,prefix:text('ok:')},{kind:'stringStartsWith',value,prefix:text('err:')})
const parse=source=>direct('journalParse',{source})
const key=prefix=>concat(text(prefix),p('token'))
const record=(state,outcome)=>concat(text('{"version":"1","id":'),quote(p('recordKey')),text(',"fingerprint":'),quote(p('fingerprint')),text(',"intent":'),quote(p('intent')),text(',"effectIdentity":'),quote(p('token')),text(',"state":'),quote(text(state)),text(',"outcome":'),quote(outcome),text('}'))
const params={host:cap('Host'),scope,acc:W,decoded:D,fingerprint:string,token:string,recordKey:string,intent:string}
const args=()=>Object.fromEntries(Object.keys(params).map(k=>[k,p(k)]))
const create=(s,name,ps,type,body,effects)=>s.create(name,ps,type,resolveFrames(body),effects)

export async function author(s,{disabled=false}={}){
 // Extend the example Host declaration with generic external storage/digest and
 // effect-correlation/evidence contracts. These are not runtime/core features.
 const additions=[['storageRead',{key:string},option(string)],['storageWrite',{key:string,value:string},unit],['digest',{value:string},string],['effectIdentity',{identity:string},unit],['effectEvidence',{identity:string},string]]
 const inherited={...s,call:async(name,args)=>{
  if(name==='request'&&args.op==='simulateCreateCapabilityByName'&&args.arguments.displayName==='Host')args={...args,arguments:{...args.arguments,operations:[...args.arguments.operations,...additions.map(([displayName,ps,resultType])=>({displayName,parameters:Object.entries(ps).map(([displayName,type])=>({displayName,type})),resultType,effect:'Io'}))]}}
  return s.call(name,args)
 },create:async(name,...rest)=>{
  if(['acceptIntegration','wireFold'].includes(name))await s.create(name+'Original',...rest)
  return s.create(name,...rest)
 }}
 await readerAuthor(inherited)
 await create(s,'journalParse',{source:string},named('WireJson'),
  letIn(direct('wireParse',{source:p('source')}),parsed=>cond(f(parsed,'WireParse','valid'),f(parsed,'WireParse','value'),construct('WireJson',{kind:text('missing'),scalar:text(''),items:L(named('WireJson')),keys:L(string)}))),[])
 const target=f(d('request'),'Request','target'),yesNo=value=>cond(value,text('true'),text('false'))
 const intentPrefix=concat(text('{"op":"integrate","target":'),{kind:'int64ToString',value:target})
 await create(s,'journalIntent',{acc:W,decoded:D},string,
  some(direct('get',{state:direct('admissionCurrent',{owner:p('acc')}),target}),entry=>
   letIn(direct('candidate',{entry}),candidate=>concat(intentPrefix,text(',"workspace":'),quote(f(entry,'Entry','workspace')),text(',"commit":'),quote(f(candidate,'Candidate','commit')),text(',"status":'),quote(f(candidate,'Candidate','status')),text(',"settled":'),yesNo(f(candidate,'Candidate','settled')),text(',"latest":'),yesNo(f(candidate,'Candidate','latest')),text(',"discarded":'),yesNo(f(candidate,'Candidate','discarded')),text(',"integrated":'),quote(f(candidate,'Candidate','integrated')),text('}'))),concat(intentPrefix,text(',"unknown":true}'))),[])
 await create(s,'journalReply',{host:cap('Host'),decoded:D,outcome:string},unit,
  io('reply',{label:text('@wire'),record:concat(text('{"jsonrpc":"2.0","id":'),d('id'),text(',"result":{"outcome":'),quote(p('outcome')),text('}}'))}))
 const reply=outcome=>letIn(direct('journalReply',{host:p('host'),decoded:p('decoded'),outcome}),()=>p('acc'))
 await create(s,'journalFinish',{...params,outcome:string},W,
  letIn(io('storageWrite',{key:p('recordKey'),value:record('done',p('outcome'))}),()=>reply(p('outcome'))))
 const finish=(outcome,overrides={})=>direct('journalFinish',{...args(),...overrides,outcome})
 // Capture the actual typed Outcome before publishing the inherited state.
 // Its request label is the stable token only on journal-controlled dispatch.
 const accept={host:cap('Host'),state:named('State'),target:{kind:'primitive',primitive:'int64'},label:string,outcome:named('Outcome')}
 const rendered={kind:'matchResult',input:f(p('outcome'),'Outcome','result'),okBody:concat(text('ok:'),{kind:'patternReference',slot:'value',scopeDepth:0}),errBody:concat(text('err:'),{kind:'patternReference',slot:'error',scopeDepth:0})}
 await create(s,'journalAcceptProtected',{...accept,item:unit},result(named('State')),
  letIn(cond({kind:'stringStartsWith',value:p('label'),prefix:text('journal:')},io('storageWrite',{key:concat(text('outcome/'),p('label')),value:rendered}),unitValue),()=>ok(direct('acceptIntegrationOriginal',Object.fromEntries(Object.keys(accept).map(k=>[k,p(k)]))))))
 await s.publish(await s.call('replace',{function:'acceptIntegration',replacement:{kind:'matchResult',input:{kind:'cancellationShield',body:'journalAcceptProtected',context:ctx(Object.fromEntries(Object.keys(accept).map(k=>[k,p(k)])))},okBody:pat(),errBody:p('state')}}))
 const request={kind:'productUpdate',type:'Request',source:d('request'),fields:[{field:'label',value:p('token')}]}
 await create(s,'journalApply',params,W,
  letIn(io('effectIdentity',{identity:p('token')}),()=>some(direct('get',{state:direct('admissionCurrent',{owner:p('acc')}),target:f(d('request'),'Request','target')}),()=>
   letIn(direct('sessionCommandFold',{host:p('host'),scope:p('scope'),acc:p('acc'),item:request,wrongBoundary:boolean(false)}),owner=>
    some(io('storageRead',{key:key('outcome/')}),outcome=>direct('journalFinish',{...args(),acc:owner,outcome}),
     letIn(direct('journalReply',{host:p('host'),decoded:p('decoded'),outcome:text('err:unresolved')}),()=>owner))),finish(text('err:unknown')))))
 await create(s,'journalReconcile',{...params,expectedIntent:string},W,
  letIn(parse(io('effectEvidence',{identity:p('token')})),evidence=>
   some(io('storageRead',{key:key('outcome/')}),outcome=>cond(outcomeValid(outcome),finish(outcome,{intent:p('expectedIntent')}),reply(text('err:unresolved'))),
    cond(and(and(stringField(evidence,'state'),eqStr(get(evidence,'state'),text('applied'))),and(stringField(evidence,'outcome'),outcomeValid(get(evidence,'outcome')))),finish(get(evidence,'outcome'),{intent:p('expectedIntent')}),
     cond(and(eqStr(get(evidence,'state'),text('absent')),eqStr(p('intent'),p('expectedIntent'))),direct('journalApply',args()),reply(text('err:unresolved')))))))
 await create(s,'journalExisting',{...params,stored:string},W,
  letIn(parse(p('stored')),stored=>
   cond(and(and(eqStr(f(stored,'WireJson','kind'),text('object')),['version','id','fingerprint','intent','effectIdentity','state','outcome'].map(key=>stringField(stored,key)).reduce(and)),and(eqStr(get(stored,'version'),text('1')),eqStr(get(stored,'id'),p('recordKey')))),
    cond(eqStr(get(stored,'fingerprint'),p('fingerprint')),
     cond(eqStr(get(stored,'effectIdentity'),p('token')),
      cond(and(eqStr(get(stored,'state'),text('done')),outcomeValid(get(stored,'outcome'))),reply(get(stored,'outcome')),
       cond(eqStr(get(stored,'state'),text('pending')),direct('journalReconcile',{...args(),expectedIntent:get(stored,'intent')}),reply(text('err:unresolved')))),reply(text('err:unresolved'))),reply(text('err:payload-conflict'))),reply(text('err:unresolved')))))
 await create(s,'journalRequest',params,W,
  some(io('storageRead',{key:p('recordKey')}),stored=>direct('journalExisting',{...args(),stored}),
   letIn(io('storageWrite',{key:p('recordKey'),value:record('pending',text(''))}),()=>direct('journalApply',args()))))
 const wire={host:cap('Host'),scope,acc:W,item:string,wrongBoundary:bool}
 const original=direct('wireFoldOriginal',Object.fromEntries(Object.keys(wire).map(k=>[k,p(k)])))
 const op=f(d('request'),'Request','op'),mode=f(d('request'),'Request','mode')
 // Notifications/null IDs and asynchronous begin-integrate have no supported
 // durable request contract. Fail closed before dispatching either form.
 await create(s,'journalRoute',{host:cap('Host'),scope,acc:W,decoded:D,source:string},W,
  cond(or(d('notification'),or(eqStr(d('id'),text('null')),and(eqStr(op,text('begin')),eqStr(mode,text('integrate'))))),
   cond(d('notification'),p('acc'),reply(text('err:stable-synchronous-id-required'))),
   letIn(io('digest',{value:p('source')}),fingerprint=>letIn(io('digest',{value:d('id')}),identity=>
    direct('journalRequest',{host:p('host'),scope:p('scope'),acc:p('acc'),decoded:p('decoded'),fingerprint,token:concat(text('journal:'),identity,text(':'),fingerprint),recordKey:concat(text('journal/v1/'),identity),intent:direct('journalIntent',{acc:p('acc'),decoded:p('decoded')})})))))
 await create(s,'journalWire',wire,W,
  letIn(direct('wireDecode',{source:p('item')}),decoded=>
   cond(and(eqStr({kind:'int64ToString',value:f(decoded,'WireDecoded','code')},text('0')),or(eqStr(f(f(decoded,'WireDecoded','request'),'Request','op'),text('integrate')),and(eqStr(f(f(decoded,'WireDecoded','request'),'Request','op'),text('begin')),eqStr(f(f(decoded,'WireDecoded','request'),'Request','mode'),text('integrate'))))),
    direct('journalRoute',{host:p('host'),scope:p('scope'),acc:p('acc'),decoded,source:p('item')}),original)))
 await s.publish(await s.call('replace',{function:'wireFold',replacement:cond(boolean(disabled),original,direct('journalWire',Object.fromEntries(Object.keys(wire).map(k=>[k,p(k)]))))}))
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,declarations,nodes:s.nodes}
}
