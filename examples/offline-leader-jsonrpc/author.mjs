import {author as sessionAuthor} from '../offline-leader-team/author.mjs'
import {parser} from './parser.mjs'
import {string,int,bool,unit,unitValue,cap,scope,list,result,ok,err,pat,bind,named,p,field,construct,text,integer,boolean,direct,cond,eqStr,eqInt,invoke,concat,letIn,some,resolveFrames,unary,add,lt,and,or,not,L,fold,ctx} from './recipes.mjs'
const J=named('WireJson'),D=named('WireDecoded'),Q=named('Request'),W=named('AdmissionOwner')
const j=(v,k)=>field(v,'WireJson',k),d=(v,k)=>field(v,'WireDecoded',k)
const get=(value,key)=>direct('wireGet',{value,key:text(key)})
const kind=(v,k)=>eqStr(j(v,'kind'),text(k))
const missing=v=>kind(v,'missing'),scalar=v=>j(v,'scalar')
const member=(v,items)=>items.map(x=>eqStr(v,text(x))).reduce(or)
const defaultRequest=()=>construct('Request',{op:text('inspect'),label:text(''),mode:text('all'),target:integer(0),previous:integer(0),limit:integer(5000)})
const decoded=(code,id=text('null'),notification=boolean(false),request=defaultRequest())=>construct('WireDecoded',{code:integer(code),id,notification,request})
const intValue=(v,fallback)=>cond(missing(v),integer(fallback),some(unary('stringParseInt64',scalar(v)),x=>x,integer(-1)))
export async function author(s){
 await sessionAuthor(s,{wireTurn:async s=>{await parser(s);await defineWire(s)}})
 await s.create('wireRun',{host:cap('Host'),turns:list(named('LeaderTurn')),wrongBoundary:bool},result(string),
  {kind:'matchResult',input:direct('sessionRun',{host:p('host'),turns:p('turns'),wrongBoundary:p('wrongBoundary')}),
   okBody:bind(invoke(p('host'),'Host','reply',{label:text('@wire-terminal'),record:concat(text('{"jsonrpc":"2.0","method":"session/terminal","params":{"report":'),direct('wireQuote',{value:pat()}),text('}}'))}),ok(pat('value',1))),errBody:err(pat('error'))})
 const declarations=await s.call('inspect',{view:'declarations'})
 const artifact=await s.call('request',{op:'emitCSharp',arguments:{profile:{kind:'externalImplementations',operationIds:declarations.capabilities.flatMap(c=>c.operations.map(o=>o.id)),concurrentCapabilityIds:declarations.capabilities.map(c=>c.id)}}})
 return {artifact,declarations,nodes:s.nodes}
}
async function defineWire(s){
 const create=(name,ps,type,body,effects=[])=>s.create(name,ps,type,resolveFrames(body),effects)
 const fields=o=>Object.entries(o).map(([displayName,type])=>({displayName,type}))
 await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[{displayName:'WireDecoded',typeKind:{kind:'product',fields:fields({code:int,id:string,notification:bool,request:Q})}}]}}))
 await create('wireQuoteChar',{item:string},string,
  ['"','\\','\b','\f','\n','\r','\t'].reduceRight((rest,k)=>cond(eqStr(p('item'),text(k)),text(({ '"':'\\"','\\':'\\\\','\b':'\\b','\f':'\\f','\n':'\\n','\r':'\\r','\t':'\\t'})[k]),rest),
   some(unary('stringToCodePoint',p('item')),code=>cond(lt(code,integer(32)),concat(text('\\u00'),cond(lt(code,integer(16)),text('0'),text('1')),some({kind:'listItemAt',list:unary('stringCodePoints',text('0123456789abcdef')),index:cond(lt(code,integer(16)),code,{kind:'int64WrappingSubtract',left:code,right:integer(16)})},x=>x,text('0'))),p('item')),p('item'))))
 await create('wireQuote',{value:string},string,concat(text('"'),{kind:'stringJoin',list:{kind:'listMap',source:unary('stringCodePoints',p('value')),function:'wireQuoteChar'},separator:text('')},text('"')))
 await create('wireAllowedKey',{acc:bool,item:string,allowed:list(string)},bool,and(p('acc'),fold(p('allowed'),boolean(false),'wireKeyHas',{key:p('item')})))
 const keys=(value,allowed)=>fold(j(value,'keys'),boolean(true),'wireAllowedKey',{allowed:L(string,...allowed.map(text))})
 await create('wireIntValid',{value:J},bool,or(missing(p('value')),and(kind(p('value'),'number'),some(unary('stringParseInt64',scalar(p('value'))),x=>not(lt(x,integer(0))),boolean(false)))))
 const a=p('arguments'),id=p('id'),notification=p('notification')
 const validInt=k=>direct('wireIntValid',{value:get(a,k)})
 const validArgs=and(kind(a,'object'),and(keys(a,['op','label','target','previous','mode','limit']),and(kind(get(a,'op'),'string'),and(kind(get(a,'label'),'string'),and(member(scalar(get(a,'op')),['spawn','inspect','wait','stop','integrate','discard','finish','begin','join','cancel','gate']),and(validInt('target'),and(validInt('previous'),and(validInt('limit'),or(missing(get(a,'mode')),kind(get(a,'mode'),'string'))))))))))
 const request=construct('Request',{op:scalar(get(a,'op')),label:scalar(get(a,'label')),target:intValue(get(a,'target'),0),previous:intValue(get(a,'previous'),0),limit:intValue(get(a,'limit'),5000),mode:cond(missing(get(a,'mode')),text('all'),scalar(get(a,'mode')))})
 await create('wireArgumentsValid',{arguments:J},bool,validArgs)
 await create('wireBind',{arguments:J,id:string,notification:bool},D,
  cond(and(direct('wireArgumentsValid',{arguments:a}),or(not(eqStr(scalar(get(a,'op')),text('begin'))),member(scalar(get(a,'mode')),['spawn','inspect','wait','stop','integrate','discard','finish']))),construct('WireDecoded',{code:integer(0),id,notification,request}),decoded(-32602,id,notification)))
 await create('wireRoute',{value:J,id:string,notification:bool},D,
  letIn(get(p('value'),'params'),params=>cond(eqStr(scalar(get(p('value'),'method')),text('ping')),
   cond(or(missing(params),and(kind(params,'object'),eqInt(unary('listLength',j(params,'keys')),integer(0)))),decoded(1,id,notification),decoded(-32602,id,notification)),
   cond(eqStr(scalar(get(p('value'),'method')),text('tools/call')),
    cond(and(kind(params,'object'),and(keys(params,['name','arguments']),eqStr(scalar(get(params,'name')),text('session_command')))),direct('wireBind',{arguments:get(params,'arguments'),id,notification}),decoded(-32602,id,notification)),decoded(-32601,id,notification)))))
 await create('wireEnvelope',{value:J},D,
  letIn(get(p('value'),'id'),id=>letIn(get(p('value'),'method'),method=>
   cond(and(kind(p('value'),'object'),and(keys(p('value'),['jsonrpc','method','params','id']),and(and(kind(get(p('value'),'jsonrpc'),'string'),eqStr(scalar(get(p('value'),'jsonrpc')),text('2.0'))),and(kind(method,'string'),or(or(missing(id),kind(id,'null')),or(kind(id,'string'),and(kind(id,'number'),some(unary('stringParseInt64',scalar(id)),_=>boolean(true),boolean(false))))))))),
    direct('wireRoute',{value:p('value'),id:cond(kind(id,'string'),direct('wireQuote',{value:scalar(id)}),cond(kind(id,'number'),some(unary('stringParseInt64',scalar(id)),x=>unary('int64ToString',x),text('null')),text('null'))),notification:missing(id)}),decoded(-32600)))))
 await create('wireDecode',{source:string},D,letIn(direct('wireParse',{source:p('source')}),parsed=>cond(field(parsed,'WireParse','valid'),direct('wireEnvelope',{value:field(parsed,'WireParse','value')}),decoded(-32700))))
 await create('wireResponse',{decoded:D},string,
  concat(text('{"jsonrpc":"2.0","id":'),d(p('decoded'),'id'),cond(lt(d(p('decoded'),'code'),integer(0)),concat(text(',"error":{"code":'),unary('int64ToString',d(p('decoded'),'code')),text(',"message":'),direct('wireQuote',{value:cond(eqInt(d(p('decoded'),'code'),integer(-32700)),text('Parse error'),cond(eqInt(d(p('decoded'),'code'),integer(-32600)),text('Invalid Request'),cond(eqInt(d(p('decoded'),'code'),integer(-32601)),text('Method not found'),text('Invalid params'))))}),text('}}')),cond(eqInt(d(p('decoded'),'code'),integer(1)),text(',"result":{}}'),text(',"result":{"dispatched":true}}')))))
 await create('wireWrite',{host:cap('Host'),decoded:D},unit,cond(d(p('decoded'),'notification'),unitValue,invoke(p('host'),'Host','reply',{label:text('@wire'),record:direct('wireResponse',{decoded:p('decoded')})})),['Io'])
 // Deliberately wrong test placement: dispatch an invalid spawn. The inherited
 // wrongBoundary flag is false in supported runs; real controls must reject true.
 const unsafe=construct('Request',{op:text('spawn'),label:text('invalid'),target:integer(0),previous:integer(0),limit:integer(5000),mode:text('all')})
 await create('wireFold',{host:cap('Host'),scope,acc:W,item:string,wrongBoundary:bool},W,
  letIn(direct('wireDecode',{source:p('item')}),decoded=>letIn(cond(or(eqInt(d(decoded,'code'),integer(0)),and(p('wrongBoundary'),lt(d(decoded,'code'),integer(0)))),direct('sessionCommandFold',{host:p('host'),scope:p('scope'),acc:p('acc'),item:cond(eqInt(d(decoded,'code'),integer(0)),d(decoded,'request'),unsafe),wrongBoundary:p('wrongBoundary')}),p('acc')),owner=>letIn(direct('wireWrite',{host:p('host'),decoded}),_=>owner))),['Io'])
 await create('wirePreviewOne',{item:string},string,letIn(direct('wireDecode',{source:p('item')}),decoded=>cond(d(decoded,'notification'),text(''),direct('wireResponse',{decoded}))))
 await create('wirePreview',{lines:list(string)},list(string),{kind:'listMap',source:p('lines'),function:'wirePreviewOne'})
 await create('wirePreviewControlOne',{item:string,wrongId:bool},string,letIn(direct('wireDecode',{source:p('item')}),decoded=>cond(d(decoded,'notification'),text(''),direct('wireResponse',{decoded:cond(p('wrongId'),{kind:'productUpdate',type:'WireDecoded',source:decoded,fields:[{field:'id',value:text('"wrong"')}]},decoded)}))))
 await create('wirePreviewControl',{lines:list(string),wrongId:bool},list(string),{kind:'listMap',source:p('lines'),function:'wirePreviewControlOne',context:ctx({wrongId:p('wrongId')})})
}
