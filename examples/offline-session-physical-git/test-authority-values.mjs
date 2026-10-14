// Authority JSON types and exact opaque-string semantics across all three
// acceptance boundaries, including retained invalid history. Offline fixtures.
import assert from 'node:assert/strict'
import {readFile,writeFile,mkdir,cp} from 'node:fs/promises'
import {join,resolve,dirname} from 'node:path'
import {spawnSync} from 'node:child_process'
import {suite,slot,store,uncertain} from './continuation-fixture.mjs'
import {hash,inspect} from './git-fixture.mjs'
const bundle=resolve(process.argv[2]),s=await suite(bundle,process.argv[3]),rows=[],contracts=[]
async function run(d,kind,request,options={}){
 const {storage=join(d.root,'state'),settings={}}=options,current=Object.hasOwn(options,'current')?options.current:request?.authorization;
 const i=++d.seq,path=join(d.root,`value-scenario-${i}.json`),scenario={...d.scenario,...settings,storageDir:storage,crashWindow:''}
 for(const key of ['dispositionRequest','continuationRequest','releaseRequest','dispositionAuthorization','continuationAuthorization','releaseAuthorization'])delete scenario[key]
 Object.assign(scenario,settings)
 if(kind){scenario[kind+'Request']=join(d.root,`value-request-${i}.json`);scenario[kind+'Authorization']=current;await writeFile(scenario[kind+'Request'],JSON.stringify(request))}
 await writeFile(path,JSON.stringify(scenario));const before=await store(storage),physicalBefore=await inspect(d.f.root)
 const x=spawnSync('dotnet',[join(bundle,'Runtime.dll'),path],{encoding:'utf8',input:'',timeout:30000});assert.equal(x.error,undefined);assert.equal(x.status,0)
 const row={name:d.name,index:i,kind,request,scenario:JSON.parse(await readFile(path)),outputs:x.stdout.trim().split('\n'),events:x.stderr.trim().split('\n').filter(Boolean).map(JSON.parse),before,after:await store(storage),physicalBefore,physicalAfter:await inspect(d.f.root)};uncertain(row.after);rows.push(row);await writeFile(join(d.root,`value-row-${i}.json`),JSON.stringify(row,null,2)+'\n');return row
}
function unchanged(r){assert.deepEqual(r.before,r.after);assert.deepEqual(r.physicalBefore,r.physicalAfter);assert.ok(!r.events.some(e=>e.operation==='physical-git-owner-start'||e.operation==='storage-write-attempt'))}
function reject(r){assert.deepEqual(r.outputs,['err:unresolved']);unchanged(r)}
async function partial(name){const d=await s.domain(name);d.name=name;const seed=await s.run(d,{prefix:true,crash:'after-git-ref'});d.scenario=seed.scenario;const c=JSON.parse(seed.after[slot('physical-child/v1')]);return {d,retention:{id:'retain-id',authorization:'offline-owner-retain',action:'retain-unresolved',invocation:c.Invocation,identity:c.Identity,repository:d.f.root,repositoryId:seed.scenario.fixtureId,launchDigest:hash(seed.after[slot('physical-child/v1')]),planDigest:hash(seed.after[slot('physical-plan/v1/'+c.Identity)]),expectedState:'ref-only'}}}
async function retain(d,q){const r=await run(d,'disposition',q);assert.match(r.outputs[0],/^ok:retained-unresolved:/);const raw=r.after[slot('physical-disposition/v1/retain-id')];return {id:'continue-id',authorization:'offline-owner-continue',action:'continue-owned-tail',retentionRequest:JSON.stringify(q),dispositionDigest:hash(raw),expectedState:'ref-only',observedStateDigest:JSON.parse(raw).stateDigest}}
async function complete(d,q,settings={dispositionAuthorization:'offline-owner-retain'}){const r=await run(d,'continuation',q,{settings});assert.match(r.outputs[0],/^ok:continued-owned-tail:/);const raw=r.after[slot('physical-continuation-result/v1/continue-id')],p=JSON.parse(raw);return {id:'release-id',authorization:'offline-owner-release',action:'release-observation-admission',continuationId:'continue-id',continuationDigest:hash(raw),observedStateDigest:p.stateDigest,registryDigest:p.registryDigest}}
const malformed=[['missing',undefined],['null',null],['number',0],['boolean',false],['object',{}],['array',[]],['empty','']]
const negatives=malformed.flatMap(([name,v])=>[[name+'-both',v,v],[name+'-request',v,'opaque-authority'],[name+'-current','opaque-authority',v]])
negatives.push(['trim-mismatch',' opaque ','opaque'],['case-mismatch','Opaque','opaque'])
const positives=['opaque-authority',' ','\t\r\n',' opaque ','null','権限']
const {d,retention}=await partial('value-boundaries');await cp(join(d.root,'state'),join(d.root,'partial'),{recursive:true})
async function cases(kind,base,from,positive=true){
 for(const [name,authorization,current] of negatives){const storage=join(d.root,kind+'-'+name);await cp(join(d.root,from),storage,{recursive:true});reject(await run(d,kind,{...base,authorization},{storage,current,settings:kind==='continuation'?{dispositionAuthorization:'offline-owner-retain'}:{}}));contracts.push({kind,name,allowed:false})}
 if(positive)for(const [i,authorization] of positives.entries()){
  const storage=join(d.root,kind+'-positive-'+i);await cp(join(d.root,from),storage,{recursive:true});const request={...base,authorization},r=await run(d,kind,request,{storage});assert.ok(r.outputs[0].startsWith('ok:'));assert.deepEqual(r.physicalBefore,r.physicalAfter)
  const replay=await run(d,kind,request,{storage});unchanged(replay);assert.deepEqual(replay.outputs,r.outputs)
  if(kind==='release'){const startup=await run(d,'',null,{storage});unchanged(startup);assert.ok(startup.events.some(e=>e.operation==='read-start'))}
  contracts.push({kind,name:'positive-'+i,authorization,allowed:true,exactWhitespace:true})
 }
}
await cases('disposition',retention,'partial')
const continuation=await retain(d,retention);await cp(join(d.root,'state'),join(d.root,'retained'),{recursive:true})
await cases('continuation',continuation,'retained',false)
const release=await complete(d,continuation);await cases('release',release,'state')
// Optional earlier settings permit absence/empty only, never malformed values.
for(const key of ['continuationAuthorization','dispositionAuthorization'])for(const [name,value] of malformed.filter(([n])=>n!=='missing'&&n!=='empty')){
 const storage=join(d.root,'optional-'+key+'-'+name);await cp(join(d.root,'state'),storage,{recursive:true});reject(await run(d,'release',release,{storage,settings:{[key]:value}}))
}
for(const [i,authority] of positives.entries()){
 const {d:p,retention:q}=await partial('continuation-positive-'+i),c=await retain(p,q);c.authorization=authority;const r=await complete(p,c);const released=await run(p,'release',{...r,authorization:'release-distinct-'+i});assert.deepEqual(released.outputs,['ok:released-observation-admission']);contracts.push({kind:'continuation',authority,allowed:true,exactWhitespace:true})
}
// Supported identifier grammar is unchanged; authority whitespace is not ID whitespace.
for(const kind of ['disposition','continuation','release'])for(const id of ['bad\n','bad\r\n','bad id','日本語']){
 const base=kind==='disposition'?retention:kind==='continuation'?continuation:release,from=kind==='disposition'?'partial':kind==='continuation'?'retained':'state',storage=join(d.root,'id-'+kind+'-'+hash(id).slice(0,8));await cp(join(d.root,from),storage,{recursive:true});reject(await run(d,kind,{...base,id},{storage}));contracts.push({kind,id,allowed:false})
}
// Inspect exact historical null release/ancestor records without rewriting them.
let legacy=0
const nullRoot=resolve(process.argv[4]),related=resolve(process.argv[5]),nullCases=JSON.parse(await readFile(join(nullRoot,'raw.json'))),ancestors=JSON.parse(await readFile(join(related,'raw.json')))
const old=[...nullCases.filter(x=>!x.contractMatches).map(x=>({name:'null-release',storage:x.scenario.storageDir,scenario:x.scenario,request:x.request,kind:'release'})),...ancestors.filter(x=>x.legacy).map(x=>({name:'null-'+x.boundary,storage:x.storage,scenario:x.scenario,request:x.continuation,kind:'continuation',release:x.release,retention:x.retention}))]
assert.equal(old.length,3)
for(const item of old){
 const root=join(s.out,item.name);await mkdir(root);const p={root,name:item.name,seq:0,scenario:item.scenario,f:{root:join(dirname(item.storage),'git')}}
 for(let i=0;i<2;i++){const r=await run(p,'',null,{storage:item.storage});unchanged(r);assert.ok(!r.events.some(e=>e.operation==='read-start'))}
 reject(await run(p,item.kind,item.request,{storage:item.storage,settings:item.name==='null-retention'?{dispositionAuthorization:null}:{}}))
 if(item.retention)reject(await run(p,'disposition',item.retention,{storage:item.storage}))
 if(item.release)reject(await run(p,'release',item.release,{storage:item.storage}))
 legacy++
}
const all=[...s.rows,...rows];await writeFile(join(s.out,'raw.json'),JSON.stringify(rows,null,2)+'\n');await writeFile(join(s.out,'application-rows.json'),JSON.stringify(s.rows,null,2)+'\n')
const proof={processes:all.length,domains:s.domains.length,sigkills:s.rows.filter(r=>r.signal==='SIGKILL').length,legacyInvalidContexts:legacy,legacyExistingDomainsReused:3,contracts,rawHash:hash(JSON.stringify(rows)),applicationRowsHash:hash(JSON.stringify(s.rows)),sources:Object.fromEntries(await Promise.all(['test-authority-values.mjs','continuation.template.cs','disposition.template.cs','compile.mjs','git-owner.mjs'].map(async file=>[file,hash(await readFile(new URL(file,import.meta.url)))]))),semantics:'Required authorities are exact nonempty JSON strings; optional earlier settings may be absent/empty strings, but not null/non-string. No trimming/case folding or new authority identifier grammar; request IDs retain ASCII full-string validation.'};await writeFile(join(s.out,'proof.json'),JSON.stringify(proof,null,2)+'\n');console.log(`PASS: ${proof.processes} applications/${proof.domains} fresh domains; all three authority boundaries and ${legacy} invalid historical contexts; exact whitespace semantics preserved`)
