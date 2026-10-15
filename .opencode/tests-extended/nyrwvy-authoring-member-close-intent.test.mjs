import test from 'node:test'
import assert from 'node:assert/strict'
import {mkdtemp,readFile,cp,copyFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {execFileSync} from 'node:child_process'
test('admitted close survives cancelled handoffs while queued close remains cancellable',{timeout:240000},async()=>{
 const root=await mkdtemp(join(tmpdir(),'nyrwvy-close-intent-')),before=join(root,'before'),after=join(root,'after'),moved=join(root,'moved')
 const run=(script,...args)=>execFileSync('node',[`examples/member-close-intent/${script}.mjs`,...args],{cwd:resolve('.'),encoding:'utf8',timeout:90000})
 try{
  run('run',before,'--baseline');run('test-runtime',before)
  run('run',after);run('test-runtime',after)
  await copyFile('examples/member-close-intent/fixtures/oracle-expected.json',join(after,'oracle-evidence.json'))
  run('prove',before,after)
  for(const [dir,name] of [[before,'nyrwvy-before.json'],[after,'nyrwvy-after.json']]){
   const expected=JSON.parse(await readFile(`examples/member-close-intent/fixtures/${name}`,'utf8'))
   assert.deepEqual(JSON.parse(await readFile(join(dir,'evidence.json'),'utf8')).map(x=>x.checks),expected)
  }
  run('run','--replay',after);run('prove',before,after,'--replay')
  await cp(after,moved,{recursive:true});run('run','--replay',moved);run('prove',before,moved,'--replay')
  for(const name of ['controller.json','Generated.cs','symbols.json','cases.json'])assert.deepEqual(await readFile(join(after,name)),await readFile(join(moved,name)))
 }finally{await rm(root,{recursive:true,force:true})}
})
