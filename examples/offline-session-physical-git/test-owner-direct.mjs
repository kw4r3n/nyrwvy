// Ordinary direct Git (no stdin-consuming wrapper) and causal old-owner control.
import assert from 'node:assert/strict'
import {spawn} from 'node:child_process'
import {mkdir,readFile,writeFile,readdir} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {fixture,inspect,hash} from './git-fixture.mjs'
const out=resolve(process.argv[2]),owner=resolve(process.argv[3]??new URL('./git-owner.mjs',import.meta.url).pathname),baseline=process.argv.includes('--baseline')
await mkdir(out,{recursive:true});assert.deepEqual(await readdir(out),[])
const rows=[]
for(const command of ['commit-tree','update-ref','read-tree'])for(let index=0;index<5;index++){
 const root=join(out,command+'-'+index),state=join(root,'state');await mkdir(state,{recursive:true});const f=await fixture(join(root,'git'))
 const args=command==='commit-tree'?['commit-tree',f.tree,'-p',f.base]:command==='update-ref'?['update-ref','refs/heads/main',f.selected,f.base]:['read-tree','--reset','-u',f.selected],input=command==='commit-tree'?'direct-owner\n':''
 const before=await inspect(f.root),outputs=[],errors=[],p=spawn(process.execPath,[owner],{stdio:['pipe','pipe','pipe']})
 const ended=new Promise((resolve,reject)=>{p.once('error',reject);p.once('close',(code,signal)=>resolve({code,signal}))});p.stdout.on('data',x=>outputs.push(x));p.stderr.on('data',x=>errors.push(x));p.stdin.on('error',()=>{})
 const c={Version:'2',State:'active',Invocation:command+'-'+index,Identity:'direct-owner',Repository:f.root,RepositoryId:await readFile(join(f.root,'.git/offline-fixture-id'),'utf8'),Args:args,InputDigest:hash(input),Pid:p.pid,ExitCode:null,InputClosed:false,OutputDrained:false,OwnerDigest:hash(await readFile(owner))},raw=JSON.stringify(c)
 await writeFile(join(state,hash('physical-child/v1')+'.json'),raw);p.stdin.end(JSON.stringify({storage:state,repository:f.root,invocation:c.Invocation,input,deadlineMs:10000}))
 const timer=setTimeout(()=>p.kill('SIGKILL'),15000);let exit;try{exit=await ended}finally{clearTimeout(timer)}
 const after=await inspect(f.root);let witness;try{witness=JSON.parse(await readFile(join(state,hash('physical-join/v1/'+c.Invocation)+'.json')))}catch{}
 const row={command,index,ownerPid:p.pid,...exit,before,after,witness,launchRaw:raw,stdout:Buffer.concat(outputs).toString(),stderr:Buffer.concat(errors).toString()};rows.push(row);await writeFile(join(root,'row.json'),JSON.stringify(row,null,2)+'\n')
 assert.equal(await readFile(join(state,hash('physical-child/v1')+'.json'),'utf8'),raw);assert.equal(exit.signal,null)
 if(command==='commit-tree'){assert.ok(after.objects.includes(witness?row.stdout.trim()+' commit':after.objects.find(x=>!before.objects.includes(x))))}
 if(command==='update-ref'){assert.equal(after.head,f.selected);assert.equal(after.worktree,before.worktree);assert.equal(after.index,before.index)}
 if(command==='read-tree'){assert.equal(after.head,f.base);assert.equal(after.worktree,'selected\n')}
 if(!baseline){assert.equal(exit.code,0);assert.ok(witness);assert.equal(witness.LaunchDigest,hash(raw));assert.equal(witness.OwnerPid,p.pid);assert.equal(witness.Invocation,c.Invocation);assert.equal(witness.ExitCode,0);assert.equal(witness.ExitSignal,'');assert.equal(witness.InputClosed,true);assert.equal(witness.OutputDrained,true);assert.equal(witness.InputDigest,hash(input));assert.equal(witness.OutputDigest,hash(Buffer.concat(outputs)));assert.equal(witness.ErrorDigest,hash(Buffer.concat(errors)))}
}
const failures=rows.filter(r=>r.command!=='commit-tree'&&r.code!==0&&!r.witness).length;if(baseline)assert.ok(failures>0,'old owner control must expose ordinary no-input join loss')
const proof={baseline,ownerProcesses:rows.length,applicationProcesses:0,domains:rows.length,noInputFailures:failures,rawHash:hash(JSON.stringify(rows)),ownerHash:hash(await readFile(owner)),sourceHash:hash(await readFile(new URL(import.meta.url)))}
await writeFile(join(out,'raw.json'),JSON.stringify(rows,null,2)+'\n');await writeFile(join(out,'proof.json'),JSON.stringify(proof,null,2)+'\n');console.log(`PASS: ${rows.length} direct owners, ${baseline?failures+' old-control no-input join failures':'all ordinary Git joins retained with actual input closure'}`)
