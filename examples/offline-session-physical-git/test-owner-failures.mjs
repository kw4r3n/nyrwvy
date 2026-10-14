// Mechanical owner-failure probes, separate from application/disposition totals.
import assert from 'node:assert/strict'
import {spawn} from 'node:child_process'
import {mkdir,readFile,writeFile,readdir,chmod} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {fixture,inspect,hash} from './git-fixture.mjs'
const out=resolve(process.argv[2]),owner=new URL('./git-owner.mjs',import.meta.url).pathname
await mkdir(out,{recursive:true});assert.deepEqual(await readdir(out),[])
const rows=[],sleep=ms=>new Promise(r=>setTimeout(r,ms)),alive=async pid=>{try{return !(await readFile('/proc/'+pid+'/stat','utf8')).split(') ')[1].startsWith('Z')}catch{return false}}
for(const mode of ['stdout-overflow','stderr-overflow','failed-start','held-descriptor','feed-failure','stdout-invalid-utf8','stderr-invalid-utf8','released-descriptor']){
 const root=join(out,mode);await mkdir(join(root,'state'),{recursive:true});await mkdir(join(root,'bin'));const f=await fixture(join(root,'git'))
 if(mode!=='failed-start'){
  const script=`#!/usr/bin/node
import fs from 'node:fs';import {spawn} from 'node:child_process';fs.readFileSync(0);
${mode.includes('descriptor')?`const h=spawn('/usr/bin/node',['-e',${JSON.stringify(mode==='released-descriptor'?'setTimeout(()=>{},1000)':'setInterval(()=>{},1000)')}],{stdio:['ignore',1,2]});h.unref();fs.writeFileSync(${JSON.stringify(join(root,'helper.json'))},JSON.stringify({pid:h.pid,rootPid:process.pid}));process.exit(0);`:mode.includes('invalid-utf8')?`const stream=${mode.startsWith('stdout')?'process.stdout':'process.stderr'};stream.write(Buffer.from([255]));`:mode==='feed-failure'?'process.exit(0);':`const stream=${mode==='stdout-overflow'?'process.stdout':'process.stderr'};stream.write('X'.repeat(1048577));setInterval(()=>{},1000);`}
`
  await writeFile(join(root,'bin/git'),mode==='feed-failure'?'#!/usr/bin/node\nprocess.exit(0);\n':script);await chmod(join(root,'bin/git'),0o755)
 }
 const before=await inspect(f.root),outputs=[],errors=[],started=Date.now(),p=spawn(process.execPath,[owner],{stdio:['pipe','pipe','pipe'],env:{...process.env,PATH:join(root,'bin')}})
 const ended=new Promise(r=>p.once('close',(code,signal)=>r({code,signal})));p.stdout.on('data',x=>outputs.push(x));p.stderr.on('data',x=>errors.push(x))
 const input=mode==='feed-failure'?'X'.repeat(1048576):'fixture\n'
 const record={Version:'2',State:'active',Invocation:'failure-'+mode,Identity:'owned-failure-'+mode,Repository:f.root,RepositoryId:await readFile(join(f.root,'.git/offline-fixture-id'),'utf8'),Args:['commit-tree',f.tree,'-p',f.base],InputDigest:hash(input),Pid:p.pid,ExitCode:null,InputClosed:false,OutputDrained:false,OwnerDigest:hash(await readFile(owner))}
 const raw=JSON.stringify(record),file=join(root,'state',hash('physical-child/v1')+'.json');await writeFile(file,raw)
 p.stdin.on('error',()=>{});p.stdin.end(JSON.stringify({storage:join(root,'state'),invocation:record.Invocation,repository:f.root,input,deadlineMs:500}))
 const timer=setTimeout(()=>p.kill('SIGKILL'),8000);let helper
 try{
  const exit=await ended,elapsedMs=Date.now()-started,released=mode==='released-descriptor';assert.equal(exit.code,released?0:mode.includes('invalid-utf8')?1:2);assert.equal(exit.signal,null);assert.equal(await readFile(file,'utf8'),raw);assert.deepEqual(await inspect(f.root),before);assert.equal(Buffer.concat(outputs).length,0)
  if(mode.includes('descriptor')){helper=JSON.parse(await readFile(join(root,'helper.json')));assert.equal(await alive(helper.pid),!released,'output descriptor closure distinguishes bounded release from held root-exit')}
  let witness
  if(released){witness=JSON.parse(await readFile(join(root,'state',hash('physical-join/v1/'+record.Invocation)+'.json')));assert.equal(witness.ExitCode,0);assert.equal(witness.InputClosed,true);assert.equal(witness.OutputDrained,true);assert.equal(witness.ChildPid,helper.rootPid);assert.equal(witness.LaunchDigest,hash(raw));assert.ok(elapsedMs>=1000&&elapsedMs<5000)}
  else assert.deepEqual(await readdir(join(root,'state')),[hash('physical-child/v1')+'.json'])
  const row={mode,ownerPid:p.pid,helper,exit,elapsedMs,witness,before,after:await inspect(f.root),launchRaw:raw,stdout:Buffer.concat(outputs).toString(),stderr:Buffer.concat(errors).toString(),witnessAbsent:!released,originalLaunchPreserved:true};rows.push(row);await writeFile(join(root,'row.json'),JSON.stringify(row,null,2)+'\n')
 }finally{clearTimeout(timer);if(helper){if(await alive(helper.pid))process.kill(helper.pid,'SIGKILL');const end=Date.now()+5000;while(await alive(helper.pid)){assert.ok(Date.now()<end);await sleep(10)}}}
}
const proof={ownerProcesses:rows.length,disposableDomains:rows.length,controlledDescriptorHelpers:2,applicationProcesses:0,ownerHash:hash(await readFile(owner)),sourceHash:hash(await readFile(new URL(import.meta.url))),rawHash:hash(JSON.stringify(rows)),rows,limits:['mechanical owner closure suite only; not application executions','large refused-feed control is not a successful saturated-input guarantee','explicitly controlled descriptor helpers; arbitrary escaped descendants remain unsupported']}
await writeFile(join(out,'proof.json'),JSON.stringify(proof,null,2)+'\n');console.log(`PASS: ${rows.length} owner closure processes / ${rows.length} domains; 7 failure witnesses absent, bounded descriptor release joins successfully`)
