import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {hash} from './git-fixture.mjs'
const [original,moved,bundle,movedBundle]=process.argv.slice(2).map(x=>resolve(x)),json=async p=>JSON.parse(await readFile(p))
const a=await json(join(original,'proof.json')),b=await json(join(moved,'proof.json')),ar=await json(join(original,'raw.json')),br=await json(join(moved,'raw.json'))
assert.equal(a.rawHash,hash(JSON.stringify(ar)));assert.equal(b.rawHash,hash(JSON.stringify(br)));assert.deepEqual(a.sources,b.sources);assert.deepEqual(a.contracts,b.contracts);assert.equal(a.processes,b.processes);assert.equal(a.domains,b.domains);assert.equal(a.sigkills,b.sigkills)
const artifacts={};for(const name of ['session.json','Generated.cs','symbols.json','declarations.json','authoring.json','Program.cs','Runtime.dll','Runtime.runtimeconfig.json']){artifacts[name]=hash(await readFile(join(bundle,name)));assert.equal(artifacts[name],hash(await readFile(join(movedBundle,name))),name)}
const physical=p=>Object.fromEntries(Object.entries(p).filter(([key])=>key!=='index'))
const normalize=r=>({name:r.name,index:r.index,outputs:r.outputs,code:r.code,signal:r.signal,actions:r.actions,physicalBefore:physical(r.physicalBefore),physicalAfter:physical(r.physicalAfter),mutations:r.events.filter(e=>e.operation==='physical-git-owner-start').map(e=>e.command),readStarted:r.events.some(e=>e.operation==='read-start')})
assert.deepEqual(ar.map(normalize),br.map(normalize));const proof={processes:a.processes+b.processes,domains:a.domains+b.domains,sigkills:a.sigkills+b.sigkills,artifacts,originalProof:hash(await readFile(join(original,'proof.json'))),movedProof:hash(await readFile(join(moved,'proof.json'))),rowsCompared:ar.length,normalization:'only cross-domain raw index stat-byte digest omitted from physical comparison; local raw storage/physical invariants remain exact; process PIDs and paths kept in raw evidence'}
await writeFile(join(original,'verified.json'),JSON.stringify(proof,null,2)+'\n');console.log(`PASS: ${ar.length} corresponding application rows and eight exact original/moved artifacts`)
