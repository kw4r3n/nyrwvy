// Token and latch audits observe actual graph handles; hosts own effects only.
import {author as owner} from '../member-owner-cancellation/author.mjs'
import {p,pat,bind,invoke,text,cond,cancellation} from '../dynamic-worker-collector/lib.mjs'
export async function author(s,{retainCloseIntent=true}={}){
 const audit=(label,record,body)=>bind(invoke(p('host'),'Host','reply',{label:text('@'+label),record}),body)
 const walk=x=>{
  if(!x||typeof x!=='object')return x
  if(Array.isArray(x))return x.map(walk)
  const value=Object.fromEntries(Object.entries(x).map(([k,v])=>[k,walk(v)]))
  if(value.kind==='taskCancel')return bind(value,value)
  if(value.kind==='oneShotComplete'&&value.cell?.kind==='fieldAccess'&&value.cell.field==='closingSignal')
   return bind(value,audit('close-publication',cond(pat(),text('accepted'),text('refused')),pat('value',1)))
  return value.kind==='capabilityInvoke'&&['memberClose','snapshot'].includes(value.operation)?
   audit(value.operation+':entry',cond(cancellation,text('cancelled'),text('clear')),bind(value,audit(value.operation,cond(cancellation,text('cancelled'),text('clear')),pat('value',1)))):value
 }
 const instrumented={...s,create:(name,params,type,body,effects)=>s.create(name,params,type,walk(body),effects)}
 return owner(instrumented,{adapted:true,recoverCompleted:true,protectDetermined:true,retainCloseIntent})
}
