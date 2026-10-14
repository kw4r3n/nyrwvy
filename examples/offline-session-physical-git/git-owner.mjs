// Opt-in controlled Linux child owner. Mechanical launch/join evidence only:
// no request outcomes, journal repair, disposition policy or automatic replay.
import fs from 'node:fs'
import {spawn} from 'node:child_process'
import {createHash} from 'node:crypto'
import {join} from 'node:path'
const digest=x=>createHash('sha256').update(x).digest('hex')
const spec=JSON.parse(fs.readFileSync(0,'utf8'))
const file=join(spec.storage,digest('physical-child/v1')+'.json')
const raw=fs.readFileSync(file,'utf8'),record=JSON.parse(raw)
if(process.platform!=='linux'||record.Version!=='2'||record.State!=='active'||record.Pid!==process.pid||record.Invocation!==spec.invocation||record.Repository!==spec.repository||record.OwnerDigest!==digest(fs.readFileSync(new URL(import.meta.url))))throw Error('controlled owner correlation')
const args=['-c','core.hooksPath='+join(record.Repository,'disabled-hooks'),'-c','commit.gpgSign=false','-c','core.fsmonitor=false','-c','core.untrackedCache=false',...record.Args]
if(record.InputDigest!==digest(spec.input)||!['commit-tree','update-ref','read-tree'].includes(record.Args[0]))throw Error('controlled owner input/command')
if(!Number.isInteger(spec.deadlineMs)||spec.deadlineMs<250||spec.deadlineMs>10000)throw Error('controlled owner deadline')
const env=Object.fromEntries(Object.entries(process.env).filter(([k])=>!k.startsWith('GIT_')))
Object.assign(env,{GIT_CONFIG_NOSYSTEM:'1',GIT_CONFIG_GLOBAL:'/dev/null',GIT_TERMINAL_PROMPT:'0',GIT_OPTIONAL_LOCKS:'0',GIT_AUTHOR_NAME:'Offline fixture',GIT_AUTHOR_EMAIL:'fixture@invalid',GIT_COMMITTER_NAME:'Offline fixture',GIT_COMMITTER_EMAIL:'fixture@invalid',GIT_AUTHOR_DATE:'2000-01-01T00:00:00Z',GIT_COMMITTER_DATE:'2000-01-01T00:00:00Z'})
const child=spawn('git',args,{cwd:record.Repository,env,stdio:['pipe','pipe','pipe']})
let inputClosed=false,inputFinished=false,failed=false,spawned=false
const chunks=[[],[]],sizes=[0,0]
child.once('spawn',()=>{spawned=true})
child.stdin.on('error',()=>{failed=true})
const inputDone=new Promise(resolve=>{
 child.stdin.once('finish',()=>{inputFinished=true})
 child.stdin.once('close',()=>{inputClosed=true;resolve()})
})
child.on('error',()=>{failed=true})
for(const [i,stream] of [child.stdout,child.stderr].entries()){
 stream.on('error',()=>{failed=true})
 stream.on('data',data=>{sizes[i]+=data.length;if(sizes[i]<=1048576)chunks[i].push(data);else{failed=true;child.kill('SIGKILL')}})
}
let drainTimer
const armDrain=()=>{drainTimer??=setTimeout(()=>process.exit(2),2000)}
const timer=setTimeout(()=>{child.kill('SIGKILL');armDrain()},spec.deadlineMs)
child.once('exit',armDrain)
// A no-input command can exit without reading stdin. Do not issue even an empty
// data write, and join the actual input descriptor: child 'close' can precede it.
if(spec.input==='')child.stdin.end();else child.stdin.end(spec.input)
const [[code,signal]]=await Promise.all([new Promise(r=>child.once('close',(c,s)=>r([c,s]))),inputDone])
clearTimeout(timer)
clearTimeout(drainTimer)
// 'close' follows root exit and closure of the owned output descriptors. Failed
// feeding/drainage/capture never publishes a join witness, even with a dead root.
if(!spawned||failed||!inputFinished||!inputClosed)process.exit(2)
const output=Buffer.concat(chunks[0]),error=Buffer.concat(chunks[1])
const decode=x=>new TextDecoder('utf8',{fatal:true}).decode(x)
decode(output);decode(error)
const receipt={Version:'1',Invocation:record.Invocation,Identity:record.Identity,Repository:record.Repository,RepositoryId:record.RepositoryId,LaunchDigest:digest(raw),OwnerDigest:record.OwnerDigest,OwnerPid:process.pid,ChildPid:child.pid,Args:record.Args,InputDigest:record.InputDigest,ExitCode:code,ExitSignal:signal??'',InputClosed:true,OutputDrained:true,OutputDigest:digest(output),ErrorDigest:digest(error)}
function atomic(key,value){const dest=join(spec.storage,digest(key)+'.json'),tmp=dest+'.tmp';const fd=fs.openSync(tmp,'w');try{fs.writeFileSync(fd,value);fs.fsyncSync(fd)}finally{fs.closeSync(fd)}fs.renameSync(tmp,dest);const dir=fs.openSync(spec.storage,'r');try{fs.fsyncSync(dir)}finally{fs.closeSync(dir)}}
atomic('physical-join/v1/'+record.Invocation,JSON.stringify(receipt))
// The launcher may have died. Its closed output pipe cannot prevent retention.
process.stdout.on('error',()=>{});process.stderr.on('error',()=>{})
process.stdout.write(output);process.stderr.write(error)
