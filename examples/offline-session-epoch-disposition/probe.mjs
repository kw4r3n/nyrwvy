import assert from 'node:assert/strict'
import {writeFile} from 'node:fs/promises'
import {join} from 'node:path'
import {setup,slot,untouched} from './fixture.mjs'
const [epochParent,parent,out]=process.argv.slice(2),s=await setup(epochParent,parent,epochParent,out)
const d=await s.seed('aggregate-gap'),baseline=await s.activate(d)
await s.runPrior(d,{frames:[d.request.memberRequest,JSON.stringify({jsonrpc:'2.0',id:'wait-fresh',method:'tools/call',params:{name:'session_command',arguments:{op:'wait',label:'wait',target:2}}})]})
await writeFile(join(d.root,'commit-release'),'release')
const applied=await s.runPrior(d,{frames:[d.request.integrationRequest],crash:'before-epoch-registry-publication'})
const replay=await s.runPrior(d,{admin:'epoch',request:d.request});untouched(replay)
assert.ok(baseline.events.some(e=>e.operation==='outcome'&&e.isOk))
assert.ok(replay.events.some(e=>e.operation==='outcome'&&e.isOk))
assert.notEqual(baseline.physicalAfter.head,applied.physicalAfter.head)
const q=await s.request(d),missing=await s.decide(d,q)
untouched(missing);assert.equal(missing.after[slot('epoch-disposition/v1/receipt')],undefined)
assert.ok(!missing.events.some(e=>e.operation==='read-start'))
await s.save();await writeFile(join(out,'reproduction.json'),JSON.stringify({base:'29544f5e2b4725df4c3e381b85b7d80cc105804a',baselineAdmission:true,appliedAdmissionReplay:true,distinctHeads:[baseline.physicalAfter.head,applied.physicalAfter.head],pendingRegistry:JSON.parse(missing.after[slot('epoch-registry/v1')]),dispositionReceiptAbsent:true,claim:'Aggregate applied describes baseline and fresh applied effect; no existing disposition route retains the third unknown. No generic primitive deficiency reproduced.'},null,2)+'\n')
console.log('PASS: existing-policy gap reproduced; third unknown stays closed without a disposition receipt')
