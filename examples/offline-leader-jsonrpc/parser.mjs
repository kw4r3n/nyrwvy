import {string,int,bool,list,named,p,field,construct,text,integer,boolean,direct,cond,eqStr,eqInt,letIn,some,resolveFrames,unary,add,sub,lt,and,or,not,L,append,slice,fold} from './recipes.mjs'
const J=named('WireJson'),P=named('WireParse')
const f=(v,k)=>field(v,'WireParse',k),j=(v,k)=>field(v,'WireJson',k)
export const json=(kind,scalar=text(''),items=L(J),keys=L(string))=>construct('WireJson',{kind:text(kind),scalar,items,keys})
const parsed=(valid,pos,value=json('null'))=>construct('WireParse',{valid,pos,value})
const fail=parsed(boolean(false),p('pos'))
const char=pos=>slice(p('source'),pos,add(pos,integer(1)))
const ws=pos=>({kind:'stringSpan',value:p('source'),set:text(' \r\n\t'),from:pos})
const length=()=>unary('stringLength',p('source'))
const rec=(fn,values)=>direct(fn,{source:p('source'),...values})
const params={source:string,pos:int,depth:int}
export async function parser(s){
 const create=(name,ps,type,body)=>s.create(name,ps,type,resolveFrames(body),[])
 const fields=o=>Object.entries(o).map(([displayName,type])=>({displayName,type}))
 await s.publish(await s.call('request',{op:'simulateCreateTypesByName',arguments:{types:[
  {displayName:'WireJson',typeKind:{kind:'product',fields:fields({kind:string,scalar:string,items:list(J),keys:list(string)})}},
  {displayName:'WireParse',typeKind:{kind:'product',fields:fields({valid:bool,pos:int,value:J})}},
 ]}}))
 // Placeholder closes the parser's ordinary recursive call graph, replaced below.
 await create('wireValue',params,P,fail)
 await create('wireScalarValid',{acc:bool,item:string},bool,and(p('acc'),some(unary('stringToCodePoint',p('item')),code=>or(lt(code,integer(55296)),lt(integer(57343),code)),boolean(false))))
 await create('wireEscape',{source:string,pos:int},P,
  letIn(char(p('pos')),c=>['"','\\','/','b','f','n','r','t'].reduceRight((rest,k)=>cond(eqStr(c,text(k)),parsed(boolean(true),add(p('pos'),integer(1)),json('string',text(({b:'\b',f:'\f',n:'\n',r:'\r',t:'\t'})[k]??k))),rest),
   cond(eqStr(c,text('u')),letIn(slice(p('source'),add(p('pos'),integer(1)),add(p('pos'),integer(5))),hex=>
    cond(and(eqInt(unary('stringLength',hex),integer(4)),eqInt({kind:'stringSpan',value:hex,set:text('0123456789abcdefABCDEF'),from:integer(0)},integer(4))),some(unary('stringParseInt64Hex',hex),code=>some(unary('stringFromCodePoint',code),value=>parsed(boolean(true),add(p('pos'),integer(5)),json('string',value)),fail),fail),fail)),fail))))
 await create('wireStringLoop',{source:string,pos:int,value:string},P,
  cond(lt(p('pos'),length()),letIn(char(p('pos')),c=>
   cond(eqStr(c,text('"')),parsed(fold(unary('stringCodePoints',p('value')),boolean(true),'wireScalarValid'),add(p('pos'),integer(1)),json('string',p('value'))),
    cond(eqStr(c,text('\\')),letIn(rec('wireEscape',{pos:add(p('pos'),integer(1))}),escaped=>
     cond(f(escaped,'valid'),rec('wireStringLoop',{pos:f(escaped,'pos'),value:{kind:'stringConcat',left:p('value'),right:j(f(escaped,'value'),'scalar')}}),fail)),
     some(unary('stringToCodePoint',c),code=>cond(lt(code,integer(32)),fail,rec('wireStringLoop',{pos:add(p('pos'),integer(1)),value:{kind:'stringConcat',left:p('value'),right:c}})),fail)))),fail))
 await create('wireAtom',{source:string,pos:int},P,
  letIn({kind:'stringSpan',value:p('source'),set:text('-+0123456789.eEabcdefghijklmnopqrstuvwxyz'),from:p('pos')},end=>
   letIn(slice(p('source'),p('pos'),end),token=>['null','true','false'].reduceRight((rest,k)=>cond(eqStr(token,text(k)),parsed(boolean(true),end,json(k==='null'?'null':'bool',token)),rest),
    // Float parser supplies grammar only; JSON additionally forbids leading zeros.
    letIn(cond(eqStr(slice(token,integer(0),integer(1)),text('-')),slice(token,integer(1),unary('stringLength',token)),token),unsigned=>
     cond(and(eqStr(slice(unsigned,integer(0),integer(1)),text('0')),and(lt(integer(1),unary('stringLength',unsigned)),{kind:'stringContains',value:text('0123456789'),substring:slice(unsigned,integer(1),integer(2))})),fail,
      some(unary('stringParseFloat64',token),_=>parsed(boolean(true),end,json('number',token)),fail)))))))
 await create('wireKeyHas',{acc:bool,item:string,key:string},bool,or(p('acc'),eqStr(p('item'),p('key'))))
 const containerParams={...params,object:bool,items:list(J),keys:list(string)}
 await create('wireContainerTail',containerParams,P,fail)
 await create('wireObjectNext',{...params,items:list(J),keys:list(string)},P,
  letIn(ws(p('pos')),pos=>cond(eqStr(char(pos),text('"')),
   letIn(rec('wireStringLoop',{pos:add(pos,integer(1)),value:text('')}),key=>
    cond(f(key,'valid'),letIn(ws(f(key,'pos')),colon=>
     cond(eqStr(char(colon),text(':')),letIn(rec('wireValue',{pos:add(colon,integer(1)),depth:p('depth')}),value=>
      cond(and(f(value,'valid'),not(fold(p('keys'),boolean(false),'wireKeyHas',{key:j(f(key,'value'),'scalar')}))),
       rec('wireContainerTail',{pos:f(value,'pos'),depth:p('depth'),object:boolean(true),items:append(p('items'),f(value,'value')),keys:append(p('keys'),j(f(key,'value'),'scalar'))}),fail)),fail)),fail)),fail)))
 await create('wireArrayNext',{...params,items:list(J),keys:list(string)},P,
  letIn(rec('wireValue',{pos:p('pos'),depth:p('depth')}),value=>cond(f(value,'valid'),rec('wireContainerTail',{pos:f(value,'pos'),depth:p('depth'),object:boolean(false),items:append(p('items'),f(value,'value')),keys:p('keys')}),fail)))
 const next=(pos)=>cond(p('object'),rec('wireObjectNext',{pos,depth:p('depth'),items:p('items'),keys:p('keys')}),rec('wireArrayNext',{pos,depth:p('depth'),items:p('items'),keys:p('keys')}))
 const tail=letIn(ws(p('pos')),pos=>cond(eqStr(char(pos),cond(p('object'),text('}'),text(']'))),parsed(boolean(true),add(pos,integer(1)),construct('WireJson',{kind:cond(p('object'),text('object'),text('array')),scalar:text(''),items:p('items'),keys:p('keys')})),cond(eqStr(char(pos),text(',')),next(add(pos,integer(1))),fail)))
 await replace(s,'wireContainerTail',tail)
 await create('wireContainer',{...params,object:bool},P,
  cond(lt(integer(16),p('depth')),fail,letIn(ws(p('pos')),pos=>cond(eqStr(char(pos),cond(p('object'),text('}'),text(']'))),parsed(boolean(true),add(pos,integer(1)),construct('WireJson',{kind:cond(p('object'),text('object'),text('array')),scalar:text(''),items:L(J),keys:L(string)})),
   cond(p('object'),rec('wireObjectNext',{pos,depth:p('depth'),items:L(J),keys:L(string)}),rec('wireArrayNext',{pos,depth:p('depth'),items:L(J),keys:L(string)}))))))
 const value=cond(lt(integer(16),p('depth')),fail,letIn(ws(p('pos')),pos=>letIn(char(pos),c=>
  cond(eqStr(c,text('"')),rec('wireStringLoop',{pos:add(pos,integer(1)),value:text('')}),
   cond(or(eqStr(c,text('{')),eqStr(c,text('['))),rec('wireContainer',{pos:add(pos,integer(1)),depth:add(p('depth'),integer(1)),object:eqStr(c,text('{'))}),rec('wireAtom',{pos}))))))
 await replace(s,'wireValue',value)
 await create('wireParse',{source:string},P,
  cond(lt(integer(4096),length()),parsed(boolean(false),integer(0)),letIn(rec('wireValue',{pos:integer(0),depth:integer(0)}),value=>construct('WireParse',{valid:and(f(value,'valid'),eqInt(ws(f(value,'pos')),length())),pos:f(value,'pos'),value:f(value,'value')}))))
 await create('wireKeyEquals',{item:string,key:string},bool,eqStr(p('item'),p('key')))
 await create('wireGet',{value:J,key:string},J,
  some({kind:'listFindIndex',source:j(p('value'),'keys'),function:'wireKeyEquals',context:[{parameter:'key',value:p('key')}]},index=>some({kind:'listItemAt',list:j(p('value'),'items'),index},value=>value,json('missing')),json('missing')))
}
export async function replace(s,name,body){await s.publish(await s.call('request',{op:'simulateReplaceBodyByName',arguments:{function:name,replacement:resolveFrames(body)}}))}
