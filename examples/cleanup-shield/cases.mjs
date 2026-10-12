export const cases = [
  {id:'ordinary',patch:true},
  ...['apply','commit','head','save'].map(cancel=>({id:`cancel-${cancel}`,patch:true,cancel})),
  ...['head','save'].map(cancel=>({id:`empty-cancel-${cancel}`,patch:false,cancel})),
  {id:'rollback',patch:true,fail:'commit'},
  ...['commit','reverse-check','reverse-apply'].map(cancel=>({id:`rollback-cancel-${cancel}`,patch:true,fail:'commit',cancel})),
  {id:'rollback-failure',patch:true,fail:'commit',rollbackFail:'reverse-apply',cancel:'reverse-apply'},
  {id:'head-failure',patch:true,fail:'head',cancel:'head'},
  {id:'save-failure',patch:true,fail:'save',cancel:'save'},
]
export const phases = c => c.patch ? c.fail==='commit' ? ['apply','commit','reverse-check',...(c.rollbackFail==='reverse-check'?[]:['reverse-apply'])] : ['apply','commit','head','save'] : ['head','save']
const sv = value => ({kind:'string',value}), bv=value=>({kind:'bool',value})
export const argsFor = (c,protected_=true) => [{kind:'fakeCapability',capability:'Probe',operations:[
  {operation:'step',cases:['apply','commit','head','save','reverse-check','reverse-apply'].map(phase=>({arguments:[{parameter:'phase',value:sv(phase)}],responses:[{kind:'return',delayMillis:'10',value:{kind:'result',okKind:'string',errorKind:'string',isOk:phase!==c.fail&&phase!==c.rollbackFail,value:phase===c.fail||phase===c.rollbackFail?phase:phase==='head'?c.patch?'integrated-head':'source-head':''}}]}))},
  {operation:'report',responses:Array.from({length:8},()=>({kind:'return',value:{kind:'unit'}}))},
  {operation:'gate',responses:[{kind:'return',value:bv(!!c.cancel),delayMillis:String(c.cancel?phases(c).indexOf(c.cancel)*10+1:1)}]},
  {operation:'release',responses:[{kind:'return',value:{kind:'unit'}}]},
  {operation:'open',responses:[]},{operation:'close',responses:[]},
]},bv(c.patch),bv(protected_)]
export function normalize(c,calls) {
  return {id:c.id,steps:calls.filter(x=>x.operation==='step').map(x=>x.arguments[0].value.value),reports:calls.filter(x=>x.operation==='report').map(x=>x.arguments[0].value.value)}
}
