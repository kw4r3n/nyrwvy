import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join} from 'node:path'
import {suite,slot,store,until,exists,alive,untouched,uncertain} from './continuation-fixture.mjs'
import {hash} from './git-fixture.mjs'
const s=await suite(process.argv[2],process.argv[3])
for(const [command,state] of [['commit-tree','object-only'],['update-ref','ref-only']]){
 const d=await s.domain(state);await s.wrapper(d);const r=await s.run(d,{prefix:true,held:command});const c=JSON.parse(r.after[slot('physical-child/v1')]),helper=JSON.parse(await readFile(join(d.root,'held.json')))
 try{
  await writeFile(join(d.root,'child-release'),'release');await until(()=>exists(join(d.root,'state',slot('physical-join/v1/'+c.Invocation))),'join');await until(async()=>!await alive(c.Pid)&&!await alive(helper.pid),'helper termination')
  await s.retain(d,state);const closed=await s.run(d);untouched(closed);uncertain(closed.after);assert.ok(!closed.events.some(e=>e.operation==='read-start'));assert.equal(JSON.parse(closed.after[slot('physical-child/v1')]).State,'active')
 }finally{await writeFile(join(d.root,'child-release'),'release');await until(async()=>!await alive(c.Pid)&&!await alive(helper.pid),'cleanup')}
}
await writeFile(join(s.out,'raw.json'),JSON.stringify(s.rows,null,2)+'\n');await writeFile(join(s.out,'proof.json'),JSON.stringify({processes:s.rows.length,domains:s.domains.length,sigkills:2,rawHash:hash(JSON.stringify(s.rows)),retentionNeverReleases:true,missingOriginalOutcomes:true,scope:'controlled offline held owners, disposable repositories; baseline only'},null,2)+'\n');console.log('PASS: both retained partial active tails stay closed before input; original outcomes remain missing')
