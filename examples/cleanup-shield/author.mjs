// Ordinary recipes; the host supplies effects and gates, never finish/rollback policy.
import {prim, gen, p, pat, bind, unit, string, unitValue, text, ok, err, cond, ctx, direct, wait, cancellation} from '../task-wait-observer/author.mjs'
const bool = prim('bool'), host = {kind:'capability', name:'Probe'}, scope = gen('taskScope')
const outcome = {kind:'named', name:'Outcome'}, result = gen('result', string, string), outer = gen('result', outcome, string)
const fields = values => Object.entries(values).map(([displayName,type]) => ({displayName,type}))
const io = (operation, values={}) => ({kind:'capabilityInvoke',capabilityValue:p('host'),capability:'Probe',operation,arguments:ctx(values)})
const field = (value, name) => ({kind:'fieldAccess', source:value, type:'Outcome', field:name})
const value = (ledger, result_) => ({kind:'productConstruct',type:'Outcome',fields:[{field:'ledger',value:ledger},{field:'result',value:result_}]})
const mr = (input,okBody,errBody) => ({kind:'matchResult',input,okBody,errBody})
const tail = name => direct(name,{host:p('host'),item:unitValue})
const fail = error => ok(value(text(''),err(error,string)))
const step = phase => direct('step',{host:p('host'),phase:text(phase)})
const cat = (left,right) => ({kind:'stringConcat',left,right})
const list = (type, ...items) => ({kind:'listConstruct',itemType:type,items})
const task = gen('task',outer)
const poll = target => wait(list(task,target),'all',{kind:'optionSome',value:{kind:'int64Literal',value:'0'}})

export async function define(create,publish,{shieldedSurface=true}={}) {
  const shield = (body, context) => shieldedSurface ? {kind:'cancellationShield',body,context:ctx(context)} : direct(body,{...context,item:unitValue})
  await publish({op:'simulateCreateTypesByName',types:[{displayName:'Outcome',typeKind:{kind:'product',fields:fields({ledger:string,result})}}]})
  await publish({op:'simulateCreateEffect',displayName:'Io'})
  await publish({op:'simulateCreateCapabilityByName',displayName:'Probe',operations:[
    ['step',{phase:string},result],['report',{message:string},unit],['gate',{},bool],['release',{},unit],
    ['open',{},gen('result',unit,string)],['close',{handle:unit},unit],
  ].map(([displayName,params,resultType])=>({displayName,parameters:fields(params),resultType,effect:'Io'}))})
  const defineIo = (name,params,type,body) => create(name,params,type,body,['Io'])
  // Effects expose cancellation as ordinary typed data. Checks are graph-owned.
  await defineIo('step',{host,phase:string},result,
    bind(io('step',{phase:p('phase')}),
      bind(io('report',{message:cat(p('phase'),cond(cancellation,text('=cancelled'),text('=clear')))}),
        cond(cancellation,err(text('cancelled'),string),pat('value',1)))))
  await defineIo('undo',{host,cause:string,item:unit},outer,
    mr(step('reverse-check'),mr(step('reverse-apply'),fail(p('cause')),fail(pat('error'))),fail(pat('error'))))
  await defineIo('recordSave',{host,head:string},outer,
    mr(step('save'),ok(value(p('head'),ok(p('head')))),ok(value(p('head'),err(pat('error'),string)))))
  await defineIo('record',{host,item:unit},outer,
    mr(step('head'),direct('recordSave',{host:p('host'),head:pat()}),fail(pat('error'))))
  await defineIo('commit',{host,protected:bool,item:unit},outer,
    mr(step('commit'),tail('record'),cond(p('protected'),shield('undo',{host:p('host'),cause:pat('error')}),direct('undo',{host:p('host'),cause:pat('error'),item:unitValue}))))
  await defineIo('finish',{host,patch:bool,protected:bool,item:unit},outer,
    cond(p('patch'),mr(step('apply'),direct('commit',{host:p('host'),protected:p('protected'),item:unitValue}),fail(pat('error'))),tail('record')))
  await defineIo('parent',{host,patch:bool,protected:bool,item:unit},outer,
    bind(cond(p('protected'),shield('finish',{host:p('host'),patch:p('patch'),protected:p('protected')}),direct('finish',{host:p('host'),patch:p('patch'),protected:p('protected'),item:unitValue})),
      mr(pat(),
        ok(value(field(pat(),'ledger'),mr(field(pat(),'result'),cond(cancellation,err(text('cancelled'),string),ok(pat())),err(pat('error'),string)))),
        err(pat('error'),outcome))))
  await create('render',{value:outcome},string,
    cat(field(p('value'),'ledger'),mr(field(p('value'),'result'),cat(text('|ok:'),pat()),cat(text('|err:'),pat('error')))))
  await defineIo('coordinate',{host,parent:task},result,
    bind(io('gate'),bind(cond(pat(),
      bind({kind:'taskCancel',task:p('parent')},
        bind(io('report',{message:{kind:'matchOption',input:{kind:'listItemAt',list:poll(p('parent')),index:{kind:'int64Literal',value:'0'}},someBody:{kind:'matchOption',input:pat(),someBody:text('parent-settled'),noneBody:text('parent-pending')},noneBody:text('missing')}}),
          bind({kind:'taskCancel',task:p('parent')},io('release')))),io('release')),
      bind(wait(list(task,p('parent'))),
        bind(io('report',{message:{kind:'matchOption',input:{kind:'listItemAt',list:pat(),index:{kind:'int64Literal',value:'0'}},someBody:{kind:'matchOption',input:pat(),someBody:mr(pat(),direct('render',{value:pat()}),cat(text('outer:'),pat('error'))),noneBody:text('pending')},noneBody:text('missing')}}),ok(text('done')))))))
  await defineIo('body',{host,scope,patch:bool,protected:bool},result,
    bind({kind:'taskStart',scope:p('scope'),worker:'parent',item:unitValue,context:ctx({host:p('host'),patch:p('patch'),protected:p('protected')})},
      direct('coordinate',{host:p('host'),parent:pat()})))
  await defineIo('run',{host,patch:bool,protected:bool},result,
    {kind:'taskScope',body:'body',context:ctx({host:p('host'),patch:p('patch'),protected:p('protected')})})
  const u={kind:'typeParameter',name:'U'},e={kind:'typeParameter',name:'E'},packet=gen('result',u,e)
  await create('genericBody',{packet,item:unit},packet,p('packet'))
  await create('genericShield',{packet},packet,shield('genericBody',{packet:p('packet')}))
  await create('maskBody',{item:unit},gen('result',bool,string),ok(cancellation))
  await create('maskProbe',{},gen('result',bool,string),shield('maskBody',{}))
  await defineIo('ownedWorker',{host,item:unit},result,step('owned'))
  await defineIo('ownedScope',{host,scope},result,
    bind({kind:'taskStart',scope:p('scope'),worker:'ownedWorker',item:unitValue,context:ctx({host:p('host')})},
      bind(io('gate'),ok(text('done')))))
  await defineIo('ownedResource',{host,handle:unit},result,
    {kind:'taskScope',body:'ownedScope',context:ctx({host:p('host')})})
  await defineIo('ownedBody',{host,item:unit},result,
    {kind:'useResource',acquire:io('open'),releaseCapability:p('host'),capability:'Probe',releaseOperation:'close',body:'ownedResource',context:ctx({host:p('host')})})
  await defineIo('ownedShield',{host},result,shield('ownedBody',{host:p('host')}))
  await create('contextBody',{before:bool,item:unit},result,
    ok(cat(cond(p('before'),text('cancelled/'),text('clear/')),cond(cancellation,text('cancelled'),text('clear')))))
  await create('contextShield',{},result,shield('contextBody',{before:cancellation}))
  await defineIo('existingBody',{host,scope,item:unit},result,
    direct('ownedScope',{host:p('host'),scope:p('scope')}))
  await defineIo('existingShield',{host,scope},result,
    shield('existingBody',{host:p('host'),scope:p('scope')}))
}
