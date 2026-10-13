// Probe-only token audits surround effects; they never choose Member policy.
import {author as owner} from '../member-owner-cancellation/author.mjs'
import {p,pat,bind,invoke,text,cond,cancellation} from '../dynamic-worker-collector/lib.mjs'
export async function author(s,{protectDetermined=true,retainCloseIntent=false}={}){
 const audit=(label,body)=>bind(invoke(p('host'),'Host','reply',{label:text('@'+label),record:cond(cancellation,text('cancelled'),text('clear'))}),body)
 const walk=x=>{
  if(!x||typeof x!=='object')return x
  if(Array.isArray(x))return x.map(walk)
  const value=Object.fromEntries(Object.entries(x).map(([k,v])=>[k,walk(v)]))
  if(value.kind==='taskCancel')return bind(value,value)
  return value.kind==='capabilityInvoke'&&['memberClose','snapshot'].includes(value.operation)?
   audit(value.operation+':entry',bind(value,audit(value.operation,pat('value',1)))):value
 }
 const instrumented={...s,create:(name,params,type,body,effects)=>s.create(name,params,type,walk(body),effects)}
 return owner(instrumented,{adapted:true,recoverCompleted:true,protectDetermined,retainCloseIntent})
}
