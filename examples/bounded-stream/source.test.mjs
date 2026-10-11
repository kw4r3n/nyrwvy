import assert from 'node:assert/strict'
import test from 'node:test'
import {author} from './author.mjs'
async function recipes(){
  const functions=new Map(),s={snapshot:'unused',nodes:{},publish:async()=>{},
    call:async(name,args)=>name==='inspect'?{capabilities:[]}:name==='request'&&args.op==='emitCSharp'?{source:'',symbols:{}}:{},
    create:async(name,parameters,resultType,body)=>functions.set(name,{name,parameters,resultType,body})}
  await author(s);return functions
}
function refs(x,fn,functions,frames=[]){
  if(!x||typeof x!=='object')return
  if(Array.isArray(x)){x.forEach(y=>refs(y,fn,functions,frames));return}
  if(x.kind==='parameter')assert.ok(Object.hasOwn(fn.parameters,x.name),`${fn.name}: ${x.name}`)
  if(x.kind==='patternReference')assert.ok(frames[x.scopeDepth??0]?.includes(x.slot),`${fn.name}: ${JSON.stringify(x)}`)
  if(['directCall','taskStart','taskScope'].includes(x.kind))assert.ok(functions.has(x.function??x.worker??x.body),fn.name)
  if(x.kind==='bind'){refs(x.value,fn,functions,frames);refs(x.body,fn,functions,[['value'],...frames]);return}
  if(x.kind==='matchOption'){refs(x.input,fn,functions,frames);refs(x.someBody,fn,functions,[['value'],...frames]);refs(x.noneBody,fn,functions,frames);return}
  for(const value of Object.values(x))refs(value,fn,functions,frames)
}
test('cursor reproducer recipes bind all functions, parameters and pattern references',async()=>{
  const f=await recipes();assert.equal(f.size,6);for(const fn of f.values())refs(fn.body,fn,f)
})
test('worker advances its own cursor without a graph history log or repeated head traversal',async()=>{
  const f=await recipes(),worker=JSON.stringify(f.get('cursorWorker'))
  assert.equal((worker.match(/"oneShotCreate"/g)??[]).length,1)
  assert.equal((worker.match(/"oneShotComplete"/g)??[]).length,1)
  assert.ok(!worker.includes('oneShotWait'));assert.ok(!worker.includes('stringConcat'))
  assert.ok(worker.includes('keepUnlisted'))
})
