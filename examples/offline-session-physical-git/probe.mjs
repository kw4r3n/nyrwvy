// Evidence for the gap before implementing the replacement effect binding.
import assert from 'node:assert/strict'
import {mkdir,writeFile,readdir} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {fixture,git,inspect} from './git-fixture.mjs'
const out=resolve(process.argv[2]);await mkdir(out,{recursive:true});assert.deepEqual(await readdir(out),[])
const rows=[]
for(const boundary of ['intent-only','object-exit','ref-exit','worktree-exit']){
 const f=await fixture(join(out,boundary)),before=await inspect(f.root)
 let commit=''
 if(boundary!=='intent-only')commit=git(f.root,['commit-tree',f.tree,'-p',f.base], 'physical probe\n')
 if(['ref-exit','worktree-exit'].includes(boundary))git(f.root,['update-ref','refs/heads/main',commit,f.base])
 if(boundary==='worktree-exit')git(f.root,['read-tree','--reset','-u',commit])
 const after=await inspect(f.root),syntheticReceiptRows=[]
 const naiveAbsence=syntheticReceiptRows.length===0,physicalUnchanged=JSON.stringify(before)===JSON.stringify(after)
 assert.equal(naiveAbsence,true);assert.equal(physicalUnchanged,boundary==='intent-only')
 rows.push({boundary,fixture:f,before,after,syntheticReceiptRows,naiveAbsence,physicalUnchanged,commit,
  childCommandsExited:true,crash:false,conclusion:physicalUnchanged?'bounded unchanged-state candidate':'missing receipt cannot authorize physical retry'})
}
await writeFile(join(out,'probe.json'),JSON.stringify({scope:'real local Git command exits; no process death in preliminary probe',rows},null,2)+'\n')
console.log('PASS: 4 disposable Git probes; missing synthetic receipt misclassifies 3 physical states')
