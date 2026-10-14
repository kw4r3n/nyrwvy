import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {hash} from './git-fixture.mjs'
const [original,moved,bundle,movedBundle]=process.argv.slice(2).map(x=>resolve(x)),json=async p=>JSON.parse(await readFile(p))
const read=async dir=>({proof:await json(join(dir,'proof.json')),rows:await json(join(dir,'raw.json')),applications:await json(join(dir,'application-rows.json'))})
const a=await read(original),b=await read(moved)
for(const x of [a,b]){assert.equal(x.proof.rawHash,hash(JSON.stringify(x.rows)));assert.equal(x.proof.applicationRowsHash,hash(JSON.stringify(x.applications)));assert.equal(x.proof.processes,x.rows.length+x.applications.length)}
assert.deepEqual(a.proof.contracts,b.proof.contracts);assert.deepEqual(a.proof.sources,b.proof.sources);assert.equal(a.proof.domains,b.proof.domains);assert.equal(a.proof.sigkills,b.proof.sigkills);assert.equal(a.proof.legacyInvalidActivationsClosed,3);assert.equal(b.proof.legacyInvalidActivationsClosed,3)
const physical=value=>Object.fromEntries(Object.entries(value).filter(([key])=>key!=='index'))
const changedKeys=r=>Object.keys({...r.before,...r.after}).filter(key=>r.before[key]!==r.after[key]).map(key=>{
 let row;try{row=JSON.parse(r.after[key])}catch{return key}
 if(row?.Invocation&&row.OwnerPid&&row.ChildPid&&row.OutputDigest){assert.equal(key,hash('physical-join/v1/'+row.Invocation)+'.json');return 'physical-join/v1/<invocation>:'+row.Args[0]}
 return key
}).sort()
const normalize=r=>({name:r.name,index:r.index,kind:r.kind,code:r.code,signal:r.signal,outputs:r.outputs,physicalBefore:physical(r.physicalBefore),physicalAfter:physical(r.physicalAfter),readStarted:r.events.some(e=>e.operation==='read-start'),changedKeys:changedKeys(r),newMutations:r.events.filter(e=>e.operation==='physical-git-owner-start').map(e=>e.command)})
assert.deepEqual(a.rows.map(normalize),b.rows.map(normalize));assert.deepEqual(a.applications.map(normalize),b.applications.map(normalize))
const artifacts={};for(const prefix of ['', 'restoration-disabled/'])for(const file of ['session.json','Generated.cs','symbols.json','declarations.json','authoring.json','Program.cs','Runtime.dll','Runtime.runtimeconfig.json','compilation.json']){const name=prefix+file;artifacts[name]=hash(await readFile(join(bundle,name)));assert.equal(artifacts[name],hash(await readFile(join(movedBundle,name))),name)}
const result={processes:a.proof.processes+b.proof.processes,domains:a.proof.domains+b.proof.domains,sigkills:a.proof.sigkills+b.proof.sigkills,legacyInvalidActivations:3,legacyReadOrReplayProcesses:18,legacyDisposableDomainsReused:1,artifacts,originalProof:hash(await readFile(join(original,'proof.json'))),movedProof:hash(await readFile(join(moved,'proof.json'))),rowsCompared:a.proof.processes,normalization:'Cross-domain raw index stat digest omitted; random join filenames mapped only after exact invocation-to-key hash validation, preserving command and multiplicity. Local storage/physical checks remain exact. PID/path observations are retained in raw evidence and do not authorize recovery.'}
await writeFile(join(original,'verified.json'),JSON.stringify(result,null,2)+'\n');console.log(`PASS: ${a.proof.processes} original/moved authority rows and 18 exact artifacts; three retained invalid activations stay closed`)
