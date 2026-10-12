import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,readFile,cp,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
import {randomUUID} from 'node:crypto'
import {openAuthoring,requestAuthoring,closeAuthoring} from '../lib/nyrwvy-authoring.ts'

test('cleanup shield preserves parent cancellation, errors, replay and ownership', {timeout:180000}, async()=>{
  const root=await mkdtemp(join(tmpdir(),'nyrwvy-shield-')),bundle=join(root,'bundle')
  const options={cwd:resolve('.'),encoding:'utf8',timeout:90000}
  const context={sessionID:randomUUID(),worktree:resolve('.')}
  try {
    execFileSync('node',['--experimental-strip-types','examples/cleanup-shield/run.mjs',bundle],options)
    const first=JSON.parse(await readFile(join(bundle,'evidence.json'),'utf8'))
    const expected=JSON.parse(await readFile(new URL('../../examples/cleanup-shield/fixtures/expected.json',import.meta.url),'utf8'))
    assert.deepEqual(first.results,expected.results)
    assert.equal(first.baseline.filter((row,i)=>JSON.stringify(row)!==JSON.stringify(first.results[i])).length,12)
    const moved=join(root,'moved')
    await cp(bundle,moved,{recursive:true})
    execFileSync('node',['--experimental-strip-types','examples/cleanup-shield/run.mjs',moved,'--replay'],options)
    assert.deepEqual(JSON.parse(await readFile(join(moved,'replay-evidence.json'),'utf8')),first)
    assert.deepEqual(await readFile(join(moved,'shield.json')),await readFile(join(bundle,'shield.json')))
    execFileSync('node',['examples/cleanup-shield/test-runtime.mjs',bundle],options)
    const runtime=JSON.parse(await readFile(join(bundle,'runtime-evidence.json'),'utf8'))
    assert.deepEqual(runtime.results,expected.results)
    assert.equal(runtime.controls.length,6)
    assert.ok(runtime.controls.every(x=>x.passed))
    assert.equal(runtime.generatedSourceUnchanged,true)
    await openAuthoring('open',join(bundle,'shield.json'),context)
    const result={kind:'builtinGeneric',generic:'result',arguments:[{kind:'primitive',primitive:'string'},{kind:'primitive',primitive:'string'}]}
    const request=async payload=>JSON.parse(await requestAuthoring(payload,context))
    const unit={kind:'primitive',primitive:'unit'}
    const proposal=await request({op:'simulateCreateFunctionByName',displayName:'wrongResultBody',parameters:[{displayName:'item',type:unit}],resultType:unit,body:{kind:'unitLiteral'},requiredEffects:[]})
    assert.equal(proposal.ok,true,JSON.stringify(proposal))
    assert.equal((await request({op:'commit',proposalId:proposal.result.proposalId})).ok,true)
    const before=await readFile(join(bundle,'shield.json'))
    // Wrong implicit input, Result shape and missing body effects fail before publication.
    for(const [body,expectedCode] of [['genericShield','CancellationShieldBodyMismatch'],['wrongResultBody','CancellationShieldResultMismatch'],['ownedBody','MissingEffectRequirement']]) {
      const reply=await request({op:'simulateCreateFunctionByName',displayName:'invalidShield',parameters:body==='ownedBody'?[{displayName:'host',type:{kind:'capability',name:'Probe'}}]:[],resultType:result,body:{kind:'cancellationShield',body,context:body==='ownedBody'?[{parameter:'host',value:{kind:'parameter',name:'host'}}]:[]},requiredEffects:[]})
      assert.equal(reply.ok,false,JSON.stringify(reply))
      assert.match(JSON.stringify(reply.error),new RegExp(expectedCode))
    }
    assert.deepEqual(await readFile(join(bundle,'shield.json')),before)
    console.log(JSON.stringify({virtual:first.results.length,real:runtime.results.length,unshieldedFailures:12,controls:runtime.controls,compilation:runtime.compilation}))
  } finally {
    await closeAuthoring(context).catch(()=>{})
    await rm(root,{recursive:true,force:true})
  }
})
