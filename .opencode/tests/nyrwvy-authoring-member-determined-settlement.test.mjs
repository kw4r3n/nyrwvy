import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,readFile,cp,copyFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('determined Member settlement masks cancellation after capture and preserves undetermined cancellation',{timeout:240000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-determined-')),before=join(root,'before'),after=join(root,'after'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/member-determined-settlement/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:90000})
 try{
  run('run',before,'--baseline');run('test-runtime',before)
  run('run',after);run('test-runtime',after)
  await copyFile('examples/member-determined-settlement/fixtures/oracle-expected.json',join(after,'oracle-evidence.json'))
  run('prove',before,after)
  const expected=JSON.parse(await readFile('examples/member-determined-settlement/fixtures/nyrwvy-protected.json','utf8'))
  const actual=JSON.parse(await readFile(join(after,'evidence.json'),'utf8')).map(x=>x.checks)
  assert.deepEqual(actual,expected)
  run('run','--replay',after);run('prove',before,after,'--replay')
  await cp(after,moved,{recursive:true});run('run','--replay',moved);run('prove',before,moved,'--replay')
  for(const name of ['controller.json','Generated.cs','symbols.json','cases.json'])assert.deepEqual(await readFile(join(after,name)),await readFile(join(moved,name)))
 }finally{await rm(root,{recursive:true,force:true})}
})
