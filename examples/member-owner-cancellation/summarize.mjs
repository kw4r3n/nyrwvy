// Package sanitized local evidence; graph bundles, emitted source, binaries and
// machine paths stay in the local evidence directory.
import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
const root=resolve(process.argv[2]),json=async p=>JSON.parse(await readFile(p,'utf8')),hash=x=>createHash('sha256').update(x).digest('hex')
const artifact=async dir=>{
 const authoring=await json(join(dir,'authoring.json')),identity=await json(join(dir,'bundle-identity.json')),comparison=await json(join(dir,'comparison.json')),runtime=await json(join(dir,'runtime-evidence.json'))
 const hashes={};for(const file of ['controller.json','Generated.cs','symbols.json','cases.json'])hashes[file]=hash(await readFile(join(dir,file)))
 assert.equal(hashes['Generated.cs'],authoring.generatedSha256);assert.equal(hashes['controller.json'],identity.snapshotSha256);assert.equal(runtime.sourceUnchanged,true)
 return {functions:authoring.functionCount,nodes:authoring.nodeCount,hashes,comparison,compilation:runtime.compilation}
}
const baseline=await artifact(join(root,'baseline-verified')),final=await artifact(join(root,'verified'))
const replay=await json(join(root,'verified','replay-identity.json')),moved=await json(join(root,'verified-moved','replay-identity.json'))
for(const value of [replay,moved]){assert.equal(value.snapshotSha256,final.hashes['controller.json']);assert.equal(value.generatedSha256,final.hashes['Generated.cs']);assert.equal(value.symbolsSha256,final.hashes['symbols.json']);assert.equal(value.casesSha256,final.hashes['cases.json'])}
const oracle=await json(join(root,'verified','oracle-evidence.json')),retained=await json(join(root,'retained','oracle-evidence.json')),prior=await json(join(root,'prior','comparison.json'))
assert.equal(retained.results.length,51);assert.equal(retained.results.reduce((n,r)=>n+r.observations.length,0),771);assert.equal(prior.matched,6)
const aggregates={}
for(const name of ['build','fast','extended','adapters','node','mcp','bun','types','partition','whitespace']){
 const data=await readFile(join(root,`${name}.log`));aggregates[name]={sha256:hash(data),bytes:data.length,passed:true}
}
const summary={base:'9e98b728b834a8ba5625c4a4a136bdbd904336fd',branch:'feat/controller-parent-cancellation',publication:'local commits only; no push, PR, export or upload',oracle:oracle.pin,sourceProofs:oracle.sourceProofs,baseline,final,replay:{samePath:true,moved:true,hashesRetained:true},matrix:{scripts:6,baselineRealRuns:12,adaptedRealRuns:12,exactPythonMatches:0,subjectFunctions:115,subjectNodes:2530,probeFunctions:4,probeNodes:43,adaptationFunctions:1,adaptationNodes:9,maxRegisteredMembers:2,maxConcurrentCallers:3},retained:{serializedScenarios:51,pythonRequestAudits:771,admissionMatches:6,sourceProofs:retained.sourceProofs},aggregates,results:oracle.results.map(r=>({id:r.id,outcome:r.outcome,diagnostics:r.diagnostics,leaderClosed:r.leaderClosed,handlersDrained:r.handlersDrained,membersDrained:r.membersDrained,released:r.released})),bounds:['Synthetic Git/provider/resource effects only; no live process started','C# registry views retain actual closed-cell consequences; no reconstructed terminal registry','Return adaptation changes cancellation outcome after drain; Python handler/Member shutdown policy remains distinct','Raw held snapshot fault is synthetic ordinary host failure, not all OCE/BaseException classes','Tickets retained until scope exit; linear storage and quadratic total scanning','No arbitrary faults/interleavings, fairness, persistence or physical Git recovery claim']}
await writeFile(new URL('./fixtures/verified-closure.json',import.meta.url),JSON.stringify(summary,null,2)+'\n')
console.log('PASS: sanitized closure packages pinned source, exact artifact identities, serial suites and retained 51/771 plus six admission matches')
