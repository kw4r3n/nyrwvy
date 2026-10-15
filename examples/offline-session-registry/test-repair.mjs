// Focused identity and contradictory-evidence controls; baseline matrix stays
// unchanged. Every process/domain is fresh, with copied inputs labeled below.
import assert from 'node:assert/strict'
import {readFile,writeFile,mkdir,readdir} from 'node:fs/promises'
import {resolve,join} from 'node:path'
import {createHash} from 'node:crypto'
import {spawn} from 'node:child_process'
import {createInterface} from 'node:readline'
const bundle=resolve(process.argv[2]),out=resolve(process.argv[3]),seedBundle=resolve(process.argv[4]??bundle)
const h=x=>createHash('sha256').update(x).digest('hex'),key=k=>h(k)+'.json',rows=[],proof=[]
await mkdir(out,{recursive:true});assert.deepEqual(await readdir(out),[],'fresh focused output required')
const command=(id,op,label,extra={})=>JSON.stringify({jsonrpc:'2.0',id,method:'tools/call',params:{name:'session_command',arguments:{op,label,...extra}}})
const integration=command('same-integration-id','integrate','held',{target:1}),distinct=command('new-integration-id','integrate','held',{target:1})
const raw=JSON.parse(await readFile(join(seedBundle,'registry-raw.json'),'utf8'))
const seed=raw.find(r=>r.name==='restoration'&&r.index===1),cancel=raw.find(r=>r.name==='cancel-after-registry-publication'&&r.index===1)
const registry=s=>JSON.parse(s[key('registry/v1')]),publication=registry(seed.storageAfter),cancelPublication=registry(cancel.storageAfter)
assert.equal(cancelPublication.outcome,'err:cancelled');assert.equal(cancel.storageAfter[key('outcome-receipt/'+cancelPublication.token)],undefined)
const snapshot=async root=>Object.fromEntries(await Promise.all((await readdir(root)).sort().map(async file=>[file,await readFile(join(root,file),'utf8')])))
const phases=r=>Object.fromEntries(['commit','head','save'].map(p=>[p,r.events.filter(e=>e.operation==='effect-persisted'&&e.phase===p).length]))
const noEffects=r=>assert.deepEqual(phases(r),{commit:0,head:0,save:0})
const noProvider=r=>assert.ok(!r.events.some(e=>['provider','workspaceCreate','snapshot'].includes(e.operation)))
const response=(r,id)=>r.outputs.map(JSON.parse).filter(x=>x.id===id).at(-1)?.result
async function execute(name,state,commands,{providers={1:'complete',2:'complete',3:'complete'}}={}){
 const root=join(out,name),storage=join(root,'state');await mkdir(storage,{recursive:true})
 for(const [file,value] of Object.entries(state))await writeFile(join(storage,file),value)
 const gate=join(root,'commit-release');await writeFile(gate,'release\n')
 const scenario={kind:'transport',patch:'diff',providers,autoMember:'true',storageDir:storage,commitGateFile:gate,journalGateFile:join(root,'journal-release'),crashWindow:'',evidence:'',snapshotCommit:''}
 await writeFile(join(root,'scenario.json'),JSON.stringify(scenario))
 const events=[],outputs=[],actions=[],waiters=new Set();let failure,exitCode,signal
 const child=spawn('dotnet',[join(bundle,'Runtime.dll'),join(root,'scenario.json')],{stdio:['pipe','pipe','pipe']}),wake=()=>{for(const w of waiters)w()}
 for(const [stream,target] of [[child.stdout,outputs],[child.stderr,events]])createInterface({input:stream}).on('line',line=>{try{target.push(target===events?JSON.parse(line):line)}catch(e){failure=e}wake()})
 child.on('error',e=>{failure=e;wake()});child.stdin.on('error',e=>{failure=e;wake()})
 const ended=new Promise(resolve=>child.once('close',(code,sig)=>{exitCode=code;signal=sig;wake();resolve()}))
 const timer=setTimeout(()=>{failure=new Error('focused deadline: '+name);child.kill('SIGKILL');wake()},20000)
 const until=async pred=>{if(pred())return;await new Promise((resolve,reject)=>{const check=()=>{if(pred()){waiters.delete(check);resolve()}else if(failure||exitCode!==undefined){waiters.delete(check);reject(failure??new Error('early exit '+name))}};waiters.add(check);check()})}
 const before=await snapshot(storage)
 try{
  await until(()=>events.some(e=>e.operation==='read-start'||e.operation==='outcome'))
  if(events.some(e=>e.operation==='read-start')){
   for(const line of commands){const offset=outputs.length;actions.push({writeHex:Buffer.from(line+'\n').toString('hex')});await new Promise((resolve,reject)=>child.stdin.write(line+'\n',e=>e?reject(e):resolve()));await until(()=>outputs.slice(offset).some(x=>JSON.parse(x).id===JSON.parse(line).id))}
   actions.push({eof:true});child.stdin.end()
  }
  await ended;if(failure)throw failure;assert.equal(exitCode,0);assert.equal(signal,null)
  const outcome=events.find(e=>e.operation==='outcome');assert.ok(outcome);assert.equal(outcome.Opened,1);assert.equal(outcome.Closed,1);assert.equal(outcome.Active,0)
  const offered=actions.filter(a=>a.writeHex).map(a=>a.writeHex).join(''),read=events.filter(e=>e.operation==='pipe-read').map(e=>e.hex.toLowerCase()).join(''),written=events.filter(e=>e.operation==='pipe-write').map(e=>e.hex.toLowerCase()).join('')
  assert.equal(offered,read);assert.equal(written,Buffer.from(outputs.map(x=>x+'\n').join('')).toString('hex'))
  const row={name,scenario,actions,events,outputs,exitCode,signal,storageBefore:before,storageAfter:await snapshot(storage)};rows.push(row);return row
 }finally{clearTimeout(timer);if(exitCode===undefined){child.kill('SIGKILL');await ended}}
}
const closed=r=>{assert.ok(!r.events.some(e=>e.operation==='read-start'));assert.equal(r.events.find(e=>e.operation==='outcome').value,'unresolved');assert.equal(r.outputs.length,0);assert.ok(!r.events.some(e=>e.operation==='storage-write-attempt'));assert.deepEqual(r.storageBefore,r.storageAfter);noEffects(r);noProvider(r)}
const rejected=command('capacity-rejected','begin','capacity-rejected',{target:3,mode:'spawn'}),admitted=command('later-admitted','begin','later-admitted',{target:4,mode:'spawn'})
const capacity=await execute('capacity-rejected-then-member-number-reused',{},[
 command('hold1','begin','hold1',{target:1,mode:'spawn'}),command('hold2','begin','hold2',{target:2,mode:'spawn'}),rejected,
 command('stop1','stop','stop1',{target:1}),admitted,command('wait3','wait','wait3',{target:3}),command('stop2','stop','stop2',{target:2})
],{providers:{1:'hold',2:'hold',3:'complete'}})
assert.ok(capacity.events.some(e=>e.operation==='reply'&&e.arguments.some(a=>a.value.value==='reject:capacity')))
const rejectedFact=JSON.parse(capacity.storageAfter[key('registry-admission/v1/'+h(JSON.stringify('capacity-rejected')))]),actual=registry(capacity.storageAfter).entries.find(e=>e.id==='3')
assert.equal(rejectedFact.member,'3');assert.notEqual(rejectedFact.identity,actual.admission)
const replay=await execute('rejected-admission-stays-unresolved',capacity.storageAfter,[rejected,admitted,command('inspect3','inspect','inspect3',{target:3})])
assert.equal(response(replay,'capacity-rejected').outcome,'err:unresolved');assert.deepEqual(response(replay,'later-admitted'),{dispatched:true});noEffects(replay);noProvider(replay);assert.deepEqual(replay.storageAfter,replay.storageBefore)
proof.push({name:'rejected-number-does-not-authenticate-another-admission',processes:2,intactFreshDomain:true,rejectedFact,actualMemberIdentity:actual.admission,replay:'err:unresolved',acceptedReplay:true,noRepeatedEffects:true,storageUnchanged:true})
const matching=await execute('matching-registry-only-cancellation-repairs',cancel.storageAfter,[integration,distinct,integration])
assert.equal(response(matching,'same-integration-id').outcome,'err:cancelled');assert.equal(response(matching,'new-integration-id').outcome,'ok:integrated-head');noEffects(matching);noProvider(matching)
proof.push({name:'matching-actual-data-repairs-exact-cancellation',processes:1,exactCancellation:true,noRepeatedEffects:true})
const mutations=[
 ['contradictory-separate-actual',cancel.storageAfter,s=>{s[key('outcome/'+cancelPublication.token)]='ok:integrated-head'}],
 ['contradictory-done-journal',seed.storageAfter,s=>{const j=JSON.parse(s[key(publication.id)]);j.outcome='err:contradiction';s[key(publication.id)]=JSON.stringify(j)}],
 ['journal-identity',seed.storageAfter,s=>{const j=JSON.parse(s[key(publication.id)]);j.id='journal/v1/wrong';s[key(publication.id)]=JSON.stringify(j)}],
 ['journal-fingerprint',seed.storageAfter,s=>{const j=JSON.parse(s[key(publication.id)]);j.fingerprint=h('wrong');s[key(publication.id)]=JSON.stringify(j)}],
 ['journal-intent',seed.storageAfter,s=>{const j=JSON.parse(s[key(publication.id)]);j.intent='{}';s[key(publication.id)]=JSON.stringify(j)}],
 ['journal-effect-identity',seed.storageAfter,s=>{const j=JSON.parse(s[key(publication.id)]);j.effectIdentity='journal:wrong';s[key(publication.id)]=JSON.stringify(j)}],
 ['journal-provenance',seed.storageAfter,s=>{const j=JSON.parse(s[key(publication.id)]);j.outcomeSource='';s[key(publication.id)]=JSON.stringify(j)}],
 ['missing-journal',cancel.storageAfter,s=>{delete s[key(cancelPublication.id)]}],
 ['malformed-separate-actual',cancel.storageAfter,s=>{s[key('outcome/'+cancelPublication.token)]='{bad'}],
 ['scalar-disagrees-with-matching-separate',seed.storageAfter,s=>{s[key('outcome-receipt/'+publication.token)]='err:contradiction'}],
 ['missing-member-with-retained-next',seed.storageAfter,s=>{const v=registry(s);v.entries=[];s[key('registry/v1')]=JSON.stringify(v)}],
 ['snapshot-identity-disagrees-with-sidecar',seed.storageAfter,s=>{const v=registry(s);v.entries[0].admission=h('wrong');v.entries[0].fingerprint=h('wrong-frame');s[key('registry/v1')]=JSON.stringify(v)}],
 ['inverse-admission-association-disagrees',seed.storageAfter,s=>{const v=registry(s),k=key('registry-admission/v1/'+v.entries[0].admission),a=JSON.parse(s[k]);a.member='2';s[k]=JSON.stringify(a)}]
]
for(const [name,source,mutate] of mutations){
 const input={...source};mutate(input);const r=await execute(name,input,[integration]);closed(r)
 proof.push({name,processes:1,constructedDamage:true,closedBeforeInput:true,noRepairWrite:true,storageUnchanged:true})
}
await writeFile(join(out,'raw.json'),JSON.stringify(rows,null,2)+'\n')
const runtimeManifest={};for(const file of ['Generated.cs','Program.cs','Runtime.dll','Runtime.runtimeconfig.json'])runtimeManifest[file]=h(await readFile(join(bundle,file)))
await writeFile(join(out,'proof.json'),JSON.stringify({processes:rows.length,proof,seedBundle,seedRawSha256:h(await readFile(join(seedBundle,'registry-raw.json'))),seedInputKind:seedBundle===bundle?'fresh baseline-matrix inputs from this repaired bundle':'reused retained baseline inputs',runtimeManifest,sourceSha256:h(await readFile(new URL(import.meta.url))),rawSha256:h(await readFile(join(out,'raw.json'))),limits:['capacity sequence is an intact freshly executed domain','contradictions are constructed damaged input copies, not natural crash traces','synthetic effects, one writer, no physical/provider/Git effects']},null,2)+'\n')
console.log(`PASS: ${rows.length} focused repair processes; ${proof.length} identity, actual-result consistency and negative contracts`)
