// Authoring conveniences only: named lexical frames compile to ordinary recipes.
export * from '../dynamic-worker-collector/lib.mjs'
let serial=0
export const ref=(frame,slot='value')=>({frame,slot})
export const letIn=(value,body)=>{const frame=++serial;return {kind:'bind',value,body:body(ref(frame)),frame}}
export const some=(input,yes,no)=>{const frame=++serial;return {kind:'matchOption',input,someBody:yes(ref(frame)),noneBody:no,frame}}
export function resolveFrames(node,frames=[]){
 if(Array.isArray(node))return node.map(x=>resolveFrames(x,frames))
 if(!node||typeof node!=='object')return node
 if(!node.kind&&node.frame){const scopeDepth=frames.indexOf(node.frame);if(scopeDepth<0)throw Error('unbound lexical frame');return {kind:'patternReference',slot:node.slot,scopeDepth}}
 const out={};for(const [key,value] of Object.entries(node)){
  if(key==='frame')continue
  const nested=(node.kind==='bind'&&key==='body')||(node.kind==='matchOption'&&['someBody','noneBody'].includes(key))
  out[key]=resolveFrames(value,nested?[node.frame,...frames]:frames)
 }return out
}
export const unary=(kind,value)=>({kind,value}),binary=(kind,left,right)=>({kind,left,right})
export const add=(a,b)=>binary('int64WrappingAdd',a,b),sub=(a,b)=>binary('int64WrappingSubtract',a,b),lt=(a,b)=>binary('int64LessThan',a,b)
export const and=(a,b)=>binary('boolAnd',a,b),or=(a,b)=>binary('boolOr',a,b),not=a=>unary('boolNot',a)
export const L=(itemType,...items)=>({kind:'listConstruct',itemType,items})
export const append=(list,item)=>({kind:'listAppendItem',list,item})
export const slice=(value,start,end)=>({kind:'stringSlice',value,start,end})
export const ctx=o=>Object.entries(o).map(([parameter,value])=>({parameter,value}))
export const fold=(source,seed,fn,context={},effect=false)=>({kind:effect?'listEffectFold':'listFold',source,seed,function:fn,context:ctx(context)})
