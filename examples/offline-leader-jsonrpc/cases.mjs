import assert from 'node:assert/strict'
import {scenarios as typedScenarios,acceptance as typedAcceptance,arg,terminal} from '../offline-leader-team/cases.mjs'
export {arg,terminal}
const error=(code,id=null)=>JSON.stringify({jsonrpc:'2.0',id,error:{code,message:({'-32700':'Parse error','-32600':'Invalid Request','-32601':'Method not found','-32602':'Invalid params'})[code]}})
const message=(args,id='x')=>({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:args}})
const accepted=id=>JSON.stringify({jsonrpc:'2.0',id,result:{dispatched:true}})
const call=(args,id='x')=>JSON.stringify(message(args,id))
const specimen={op:'spawn',label:'invalid'}
export function parserCases(){
 const rows=[],add=(source,expected)=>rows.push({source,expected})
 for(const source of ['', '{', 'true false', '{"a":1,}', '[1,]', '{"x":01}', '{"x":1.}', '{"x":NaN}', '{"x":"\\x00"}', '{"x":"\\u12zz"}', '{"x":"\\u-000"}', '{"x":"\\ud800"}', '{"x":"\\udc00"}', '{"x":"raw\ncontrol"}', '{"x":1,"x":2}', '{"x":-}', ' '.repeat(4097), '['.repeat(18)+'0'+']'.repeat(18)])add(source,error(-32700))
 for(const value of [null,true,[],1,'x',{}, {method:'ping'}, {jsonrpc:'1.0',method:'ping'}, {jsonrpc:'2.0',method:1}, {jsonrpc:'2.0',method:'ping',id:true}, {jsonrpc:'2.0',method:'ping',id:[]}, {jsonrpc:'2.0',method:'ping',id:1.5}, {jsonrpc:'2.0',method:'ping',id:9223372036854776000}, {jsonrpc:'2.0',method:'ping',extra:1}])add(JSON.stringify(value),error(-32600))
 add(JSON.stringify({jsonrpc:'2.0',id:7,method:'unknown'}),error(-32601,7))
 for(const params of [null,[],1,'x',{name:'unknown',arguments:specimen},{name:5,arguments:specimen},{name:'session_command'},{name:'session_command',arguments:null},{name:'session_command',arguments:[]},{name:'session_command',arguments:specimen,extra:1}])add(JSON.stringify({jsonrpc:'2.0',id:'x',method:'tools/call',params}),error(-32602,'x'))
 for(const args of [{op:'spawn'},{label:'invalid'},{...specimen,extra:1},{...specimen,self:1},{...specimen,op:'unknown'},{...specimen,op:1},{...specimen,label:1},...['target','previous','limit'].flatMap(k=>[true,'1',1.5,-1,null].map(v=>({...specimen,[k]:v}))),{...specimen,mode:1},{...specimen,op:'begin',mode:'unknown'}])add(call(args),error(-32602,'x'))
 for(const id of [null,0,-7,9223372036854775807n,'é😀"\\\n\u0001']){
  const source=typeof id==='bigint'?'{"jsonrpc":"2.0","id":9223372036854775807,"method":"ping"}':JSON.stringify({jsonrpc:'2.0',id,method:'ping'})
  add(source,typeof id==='bigint'?'{"jsonrpc":"2.0","id":9223372036854775807,"result":{}}':JSON.stringify({jsonrpc:'2.0',id,result:{}}))
 }
 add(' \t{"jsonrpc":"2.0","id":"\\u00e9\\ud83d\\ude00","method":"ping","params":{}}\r\n',JSON.stringify({jsonrpc:'2.0',id:'é😀',result:{}}))
 const ping='{"jsonrpc":"2.0","id":0,"method":"ping"}'
 add(ping+' '.repeat(4096-ping.length),'{"jsonrpc":"2.0","id":0,"result":{}}')
 add('['.repeat(16)+'0'+']'.repeat(16),error(-32600))
 add('['.repeat(17)+']'.repeat(17),error(-32700))
 add(JSON.stringify({jsonrpc:'2.0',id:'\b\f\n\r\t/\u0000\u001f',method:'ping'}),JSON.stringify({jsonrpc:'2.0',id:'\b\f\n\r\t/\u0000\u001f',result:{}}))
 for(const params of [null,[],{extra:1}])add(JSON.stringify({jsonrpc:'2.0',id:4,method:'ping',params}),error(-32602,4))
 add(JSON.stringify({jsonrpc:'2.0',method:'ping'}),'')
 add(JSON.stringify({jsonrpc:'2.0',method:'unknown'}),'')
 add(JSON.stringify({jsonrpc:'2.0',method:'tools/call',params:{name:'session_command',arguments:{op:'spawn'}}}),'')
 return rows
}
export function scenarios(){
 const preface=parserCases().map(x=>x.source)
 return typedScenarios().map(c=>{
  let id=0
  const turns=c.turns.map((t,index)=>({label:t.label,lines:[...(index===0?preface:[]),...t.commands.map(q=>{
   const rpc=message(q,++id)
   // Gates are legitimate notifications: graph dispatch runs, wire reply omitted.
   if(q.op==='gate'||q.label==='b')delete rpc.id
   return JSON.stringify(rpc)
  })]}))
  const expected=[...(c.turns.length?parserCases().map(x=>x.expected).filter(Boolean):[])]
  id=0;for(const t of c.turns)for(const q of t.commands){id++;if(q.op!=='gate'&&q.label!=='b')expected.push(accepted(id))}
  return {...c,turns,wireExpected:expected}
 })
}
export function acceptance(c,row){
 const proof=typedAcceptance(c,row),calls=row.response.result.calls
 const wire=calls.filter(x=>x.operation==='reply'&&arg(x,'label')==='@wire').map(x=>arg(x,'record'))
 assert.deepEqual(wire,c.wireExpected,'exact serialized responses and notification suppression')
 const firstStart=calls.findIndex(x=>x.operation==='gate'&&arg(x,'label')==='request-start:a')
 const errors=calls.slice(0,firstStart).filter(x=>x.operation==='reply'&&arg(x,'label')==='@wire'&&JSON.parse(arg(x,'record')).error)
 assert.equal(errors.length,parserCases().filter(x=>x.expected.includes('"error"')).length,'invalid lines rejected before first registration')
 assert.ok(!calls.slice(0,firstStart).some(x=>['workspaceCreate','provider','step'].includes(x.operation)))
 assert.deepEqual(calls.filter(x=>x.operation==='reply'&&arg(x,'label')==='@wire-terminal').map(x=>arg(x,'record')),[JSON.stringify({jsonrpc:'2.0',method:'session/terminal',params:{report:terminal}})])
 assert.ok(calls.findIndex(x=>x.operation==='close')<calls.findIndex(x=>x.operation==='reply'&&arg(x,'label')==='@wire-terminal'))
 return {...proof,wireResponses:wire.length,invalidBeforeAdmission:errors.length,notificationSpawn:true}
}
