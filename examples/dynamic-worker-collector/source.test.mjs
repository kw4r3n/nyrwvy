// Cloud-usable source/oracle checks. These are not graph semantic or runtime tests.
import assert from 'node:assert/strict'
import test from 'node:test'
import {author} from './author.mjs'
import {makeCases,checkCase} from './cases.mjs'

async function recipes() {
  const functions=new Map(),s={snapshot:'unused',nodes:{},publish:async()=>{},
    call:async(name,args)=>name==='inspect'?{capabilities:[]}:name==='request'&&args.op==='emitCSharp'?{source:'',symbols:{}}:{},
    create:async(name,parameters,resultType,body)=>functions.set(name,{name,parameters,resultType,body})}
  await author(s)
  return functions
}
function checkRefs(x,fn,functions,frames=[]) {
  if(!x||typeof x!=='object')return
  if(Array.isArray(x)){x.forEach(y=>checkRefs(y,fn,functions,frames));return}
  if(x.kind==='parameter')assert.ok(Object.hasOwn(fn.parameters,x.name),`${fn.name}: parameter ${x.name}`)
  if(x.kind==='patternReference')assert.ok(frames[x.scopeDepth??0]?.includes(x.slot),`${fn.name}: ${JSON.stringify(x)}`)
  if(['directCall','taskStart','taskScope'].includes(x.kind))assert.ok(functions.has(x.function??x.worker??x.body),fn.name)
  if(x.kind==='bind'){checkRefs(x.value,fn,functions,frames);checkRefs(x.body,fn,functions,[['value'],...frames]);return}
  if(x.kind==='matchOption'){checkRefs(x.input,fn,functions,frames);checkRefs(x.someBody,fn,functions,[['value'],...frames]);checkRefs(x.noneBody,fn,functions,frames);return}
  if(x.kind==='matchResult'){checkRefs(x.input,fn,functions,frames);checkRefs(x.okBody,fn,functions,[['value'],...frames]);checkRefs(x.errBody,fn,functions,[['error'],...frames]);return}
  for(const value of Object.values(x))checkRefs(value,fn,functions,frames)
}
test('all recipe names and lexical pattern references are bound',async()=>{
  const functions=await recipes()
  assert.equal(functions.size,18)
  for(const fn of functions.values())checkRefs(fn.body,fn,functions)
})
test('logical append allocates once; retry loop does not allocate or yield through host',async()=>{
  const functions=await recipes(),append=JSON.stringify(functions.get('append').body),retry=JSON.stringify(functions.get('appendTry').body)
  assert.equal((append.match(/"oneShotCreate"/g)??[]).length,1)
  assert.ok(!retry.includes('oneShotCreate'))
  assert.ok(!retry.includes('capabilityInvoke'))
  assert.ok(retry.includes('oneShotComplete')&&retry.includes('oneShotWait'))
})
test('fake work scripts are argument-keyed and cover 2–4 worker joins',()=>{
  const cases=makeCases()
  assert.equal(cases.length,8)
  assert.deepEqual(cases.slice(0,3).map(c=>c.count),[2,3,4])
  for(const c of cases){const pause=c.arguments[0].operations.find(o=>o.operation==='pause');assert.ok(pause.cases);assert.equal(new Set(pause.cases.map(x=>JSON.stringify(x.arguments))).size,pause.cases.length)}
})
const arg=(parameter,value)=>({parameter,value:{kind:typeof value==='boolean'?'bool':'string',value}})
function synthetic(log='w1:register:0;w1:progress:1;w1:progress:2;w1:final:3;w2:register:0;w2:progress:1;w2:progress:2;w2:final:3;') {
  const accepted='w1:register:0;w1:progress:1;w1:progress:2;w1:final:3;w2:register:0;w2:progress:1;w2:progress:2;w2:final:3;'.split(';').filter(Boolean)
  const calls=[{operation:'open'},{operation:'ready'},...['w1','w2'].map(id=>({operation:'pause',arguments:[arg('id',id),arg('phase','join')]})),...accepted.map(event=>{const[id,kind,sequence]=event.split(':');return{operation:'observe',arguments:[arg('id',id),arg('kind',kind),arg('sequence',sequence),arg('accepted',true)]}}),{operation:'save'},{operation:'close'}]
  return {ok:true,result:{actual:{kind:'result',isOk:true,value:log},calls}}
}
test('acceptance oracle accepts cross-worker reordering but rejects loss and duplication',()=>{
  const c={id:'synthetic',count:2,mode:'normal'},response=synthetic()
  checkCase(c,response)
  const reordered=synthetic('w2:register:0;w2:progress:1;w2:progress:2;w2:final:3;w1:register:0;w1:progress:1;w1:progress:2;w1:final:3;')
  checkCase(c,reordered)
  assert.throws(()=>checkCase(c,synthetic(response.result.actual.value.replace('w1:progress:1;',''))))
  assert.throws(()=>checkCase(c,synthetic(response.result.actual.value+'w1:register:0;')))
})
test('acceptance oracle rejects per-worker reordering even with identical multiset',()=>{
  const bad=synthetic('w1:register:0;w1:progress:2;w1:progress:1;w1:final:3;w2:register:0;w2:progress:1;w2:progress:2;w2:final:3;')
  assert.throws(()=>checkCase({id:'synthetic',count:2,mode:'normal'},bad))
})
