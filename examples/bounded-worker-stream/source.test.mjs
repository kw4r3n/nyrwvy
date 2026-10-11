import assert from 'node:assert/strict'
import test from 'node:test'
import {author} from './author.mjs'
import {makeCases,validateFixtureBudget} from './cases.mjs'
export async function recipes(){
  const functions=new Map(),s={snapshot:'unused',nodes:{},publish:async()=>{},call:async(name,args)=>name==='inspect'?{capabilities:[]}:name==='request'&&args.op==='emitCSharp'?{source:'',symbols:{}}:{},create:async(name,parameters,resultType,body,requiredEffects)=>functions.set(name,{name,parameters,resultType,body,requiredEffects})}
  await author(s);return functions
}
function refs(x,fn,functions,frames=[]){
  if(!x||typeof x!=='object')return
  if(Array.isArray(x)){x.forEach(y=>refs(y,fn,functions,frames));return}
  if(x.kind==='parameter')assert.ok(Object.hasOwn(fn.parameters,x.name),`${fn.name}: ${x.name}`)
  if(x.kind==='patternReference')assert.ok(frames[x.scopeDepth??0]?.includes(x.slot),`${fn.name}: ${JSON.stringify(x)}; ${JSON.stringify(frames)}`)
  if(['directCall','taskStart','taskScope'].includes(x.kind))assert.ok(functions.has(x.function??x.worker??x.body),fn.name)
  if(x.kind==='bind'){refs(x.value,fn,functions,frames);refs(x.body,fn,functions,[['value'],...frames]);return}
  if(x.kind==='matchOption'){refs(x.input,fn,functions,frames);refs(x.someBody,fn,functions,[['value'],...frames]);refs(x.noneBody,fn,functions,[[],...frames]);return}
  if(x.kind==='matchResult'){refs(x.input,fn,functions,frames);refs(x.okBody,fn,functions,[['value'],...frames]);refs(x.errBody,fn,functions,[['error'],...frames]);return}
  for(const v of Object.values(x))refs(v,fn,functions,frames)
}
function nodes(x,kind){if(!x||typeof x!=='object')return [];return [...(x.kind===kind?[x]:[]),...Object.values(x).flatMap(y=>nodes(y,kind))]}
test('all recipe function/parameter/lexical references are bound',async()=>{const f=await recipes();for(const fn of f.values())refs(fn.body,fn,f);assert.equal(f.size,21)})
test('collector has one common self-tail site and no retained link/log variables',async()=>{const f=await recipes(),body=f.get('collector').body;assert.equal(nodes(body,'directCall').filter(x=>x.function==='collector').length,1);assert.equal(nodes(body,'oneShotWait').length,1);assert.equal(nodes(body,'stringConcat').length,0)})
test('launch returns only task handles and no coordination wait retains initial heads',async()=>{const f=await recipes();for(const n of ['launch','launchWithHeads']){assert.equal(nodes(f.get(n).body,'taskWait').length,0);assert.equal(f.get(n).resultType.generic,'list');assert.equal(f.get(n).resultType.arguments[0].generic,'task')}})
test('logical append allocates once for next and ack; retry allocates neither',async()=>{const f=await recipes();assert.equal(nodes(f.get('append').body,'oneShotCreate').length,2);assert.equal(nodes(f.get('appendTry').body,'oneShotCreate').length,0);assert.equal(nodes(f.get('appendTry').body,'capabilityInvoke').length,0)})
test('saved fixture matrix covers large and asymmetric runs plus shutdown/faults',()=>{const c=makeCases();assert.equal(c.length,9);assert.ok(c.some(x=>x.leftCount===1&&x.rightCount===128));assert.ok(c.some(x=>x.leftCount===64));assert.ok(c.some(x=>x.mode==='parent-exit'));for(const x of c)for(const op of x.arguments[0].operations.filter(x=>x.cases))assert.equal(new Set(op.cases.map(x=>JSON.stringify(x.arguments))).size,op.cases.length)})
test('start gate opens only after head-owning launcher returns',async()=>{
  const f=await recipes(),body=f.get('scopeBody').body
  assert.equal(body.kind,'bind');assert.equal(body.value.kind,'oneShotCreate')
  assert.equal(body.body.kind,'bind');assert.equal(body.body.value.function,'launch')
  assert.equal(body.body.body.kind,'bind');assert.equal(body.body.body.value.kind,'oneShotComplete')
  for(const name of ['producer','collector'])assert.equal(f.get(name).body.input.cell.name,'gate')
})
test('MatchOption None introduces an empty lexical frame, even without a payload slot',()=>{
  const fn={name:'none-scope',parameters:{}}
  const body=depth=>({kind:'bind',value:{kind:'unitLiteral'},body:{kind:'matchOption',input:{kind:'optionNone'},someBody:{kind:'unitLiteral'},noneBody:{kind:'patternReference',slot:'value',scopeDepth:depth}}})
  assert.throws(()=>refs(body(0),fn,new Map()),/none-scope/)
  assert.doesNotThrow(()=>refs(body(1),fn,new Map()))
})
test('fake scalar Result strings use flat kinds/payload; Unit results remain typed nested values',()=>{
  for(const c of makeCases()){
    const ops=c.arguments[0].operations,reads=ops.find(x=>x.operation==='read').cases
    for(const script of reads)for(const response of script.responses){
      assert.equal(response.value.kind,'result');assert.equal(response.value.okKind,'string');assert.equal(response.value.errorKind,'string');assert.equal(typeof response.value.value,'string');assert.ok(!('okType' in response.value))
    }
    for(const script of ops.find(x=>x.operation==='write').cases)for(const response of script.responses){
      if(response.kind==='throw')continue
      assert.deepEqual(response.value.okType,{kind:'primitive',primitive:'unit'});assert.equal(typeof response.value.value,'object');assert.ok(!('okKind' in response.value))
    }
  }
})
test('all virtual fixtures honor the existing 256-case/response operation budget',()=>{
  for(const c of makeCases())assert.doesNotThrow(()=>validateFixtureBudget(c.arguments[0]))
  assert.doesNotThrow(()=>validateFixtureBudget({operations:[{operation:'boundary',responses:Array(256).fill({})}]}))
  assert.throws(()=>validateFixtureBudget({operations:[{operation:'too-many',responses:Array(257).fill({})}]}),/256 total/)
  assert.throws(()=>validateFixtureBudget({operations:[{operation:'too-many-cases',cases:Array.from({length:257},()=>({responses:[]}))}]}),/256 argument/)
})
