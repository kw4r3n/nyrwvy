import {author as wireAuthor} from '../offline-leader-jsonrpc/author.mjs'
import {cap,scope,named,string,bool,option,p,pat,bind,invoke,direct,matchOption} from '../dynamic-worker-collector/lib.mjs'

// The host performs only cancellable JSONL pipe reads. The graph retains the
// owner, dispatches decoded lines and reaches the inherited finish path at EOF.
export async function author(s){
 const parameters={transport:cap('Transport')}
 return wireAuthor(s,{sessionInput:{parameters,
  async define(s){
   await s.publish(await s.call('request',{op:'simulateCreateCapabilityByName',arguments:{displayName:'Transport',operations:[{displayName:'readLine',parameters:[],resultType:option(string),effect:'Io'}]}}))
   const ps={host:cap('Host'),transport:cap('Transport'),scope,owner:named('AdmissionOwner'),wrongBoundary:bool}
   await s.create('transportLoop',ps,named('AdmissionOwner'),p('owner'))
   await s.publish(await s.call('replace',{function:'transportLoop',replacement:
    matchOption(invoke(p('transport'),'Transport','readLine'),
     bind(direct('wireFold',{host:p('host'),scope:p('scope'),acc:p('owner'),item:pat(),wrongBoundary:p('wrongBoundary')}),
      {...direct('transportLoop',{owner:pat()}),keepUnlisted:true}),p('owner'))}))
  },
  fold:owner=>direct('transportLoop',{host:p('host'),transport:p('transport'),scope:p('scope'),owner,wrongBoundary:p('wrongBoundary')})
 }})
}
