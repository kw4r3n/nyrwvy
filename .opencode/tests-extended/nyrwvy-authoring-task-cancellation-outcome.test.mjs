import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,readFile,readdir,writeFile,cp,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'

test('worker-local cancellation adapter settles Result; wait-only catch retains child fault and scope propagation',{timeout:180000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-task-outcomes-')),bundle=join(root,'bundle'),moved=join(root,'moved')
 const options={cwd:resolve('.'),encoding:'utf8',timeout:90000}
 const run=(script,...args)=>execFileSync('node',[`examples/task-cancellation-outcome/${script}.mjs`,...args],options)
 const json=async path=>JSON.parse(await readFile(path,'utf8'))
 try{
  run('run',bundle)
  const first=await json(join(bundle,'evidence.json'));assert.equal(first.length,12)
  run('test-runtime',bundle)
  const real=await json(join(bundle,'runtime-evidence.json'));assert.equal(real.controls.length,44)
  assert.ok(real.controls.every(c=>c.passed));assert.equal(real.sourceUnchanged,true)
  await cp(bundle,moved,{recursive:true})
  run('run','--replay',moved)
  assert.deepEqual(await json(join(moved,'replay-evidence.json')),first)
  run('test-runtime',moved)
  assert.deepEqual(await json(join(moved,'runtime-evidence.json')),real)
  for(const name of ['task-outcomes.nyrec','Generated.cs','symbols.json','Runtime.dll'])assert.deepEqual(await readFile(join(bundle,name)),await readFile(join(moved,name)))
  // The same saved cases replay on the Zig backend once the sidecar names it.
  for(const name of (await readdir(moved)).filter(n=>n.endsWith('.backends.json')))await writeFile(join(moved,name),'{"targets": ["zig"]}\n')
  run('run','--replay',moved)
  // Failure frames name generated symbols and differ by backend (backend-targets-and-parity.md).
  const noFrames=v=>JSON.parse(JSON.stringify(v,(k,x)=>k==='frames'?undefined:x))
  assert.deepEqual(noFrames(await json(join(moved,'replay-evidence.json'))),noFrames(first))
 }finally{await rm(root,{recursive:true,force:true})}
})
