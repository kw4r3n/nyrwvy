import assert from 'node:assert/strict'
import {readFile,writeFile} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {createHash} from 'node:crypto'
import {execFileSync} from 'node:child_process'
import {compileRuntime} from '../dynamic-worker-collector/compile-runtime.mjs'
import {acceptance,arg,parserCases} from './cases.mjs'
const dir=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const d=JSON.parse(await readFile(join(dir,'declarations.json'),'utf8')),symbols=JSON.parse(await readFile(join(dir,'symbols.json'),'utf8'))
const find=(items,name)=>items.find(x=>x.displayName===name),symbol=(kind,id)=>symbols[kind].find(x=>x.id===id).name
const host=find(d.capabilities,'Host'),run=find(d.functions,'wireRun'),boundary=find(d.functions,'sessionBoundaryProbe'),request=find(d.types,'Request'),turn=find(d.types,'LeaderTurn'),preview=find(d.functions,'wirePreviewControl')
const csType=t=>t.kind==='primitive'?({string:'NyStr',unit:'NyUnit',int64:'long',bool:'bool'})[t.primitive]:`${({result:'NyResult',list:'NyList'})[t.generic]}<${t.arguments.map(csType).join(',')}>`
const effectNames={open:'Open',close:'Close',workspaceCreate:'WorkspaceCreate',workspaceDiscard:'WorkspaceDiscard',provider:'Provider',memberClose:'MemberClose',snapshot:'Snapshot',checks:'Checks',gate:'Gate',reply:'Reply',step:'Step'}
const requestArgs=request.typeKind.fields.map(f=>`${symbol('fields',f.id)}: ${f.type.primitive==='string'?`(NyStr)r.GetProperty("${f.displayName}").GetString()!`:`r.GetProperty("${f.displayName}").GetInt64()`}`).join(',')
const replacements={HOST:symbol('capabilities',host.id),RUN:symbol('functions',run.id),RUN_HOST:symbol('parameters',find(run.parameters,'host').id),RUN_TURNS:symbol('parameters',find(run.parameters,'turns').id),RUN_WRONG:symbol('parameters',find(run.parameters,'wrongBoundary').id),
 PREVIEW:symbol('functions',preview.id),PREVIEW_LINES:symbol('parameters',find(preview.parameters,'lines').id),PREVIEW_WRONG:symbol('parameters',find(preview.parameters,'wrongId').id),
 BOUNDARY:symbol('functions',boundary.id),BOUNDARY_HOST:symbol('parameters',find(boundary.parameters,'host').id),BOUNDARY_WRONG:symbol('parameters',find(boundary.parameters,'wrongBoundary').id),TURN:symbol('types',turn.id),
 TURNARGS:turn.typeKind.fields.map(f=>`${symbol('fields',f.id)}: ${f.displayName==='label'?'(NyStr)t.GetProperty("label").GetString()!':'NyList<NyStr>.Create(t.GetProperty("lines").EnumerateArray().Select(r=>(NyStr)r.GetString()!).ToArray())'}`).join(','),
 METHODS:host.operations.map(o=>`public ${csType(o.resultType)} ${symbol('capabilityOperations',o.id)}(${o.parameters.map(p=>`${csType(p.type)} ${p.displayName==='base'?'basis':p.displayName}`).join(',')}) => ${effectNames[o.displayName]}(${o.parameters.map(p=>p.displayName==='base'?'basis':p.displayName).join(',')});`).join('\n')}
const program=(await readFile(new URL('./runtime.template.cs',import.meta.url),'utf8')).replace(/@@([A-Z_]+)@@/g,(_,key)=>{assert.ok(replacements[key],key);return replacements[key]})
const original=hash(await readFile(join(dir,'Generated.cs')))
await writeFile(join(dir,'Program.cs'),program)
const options={cwd:dir,encoding:'utf8',timeout:90000}
const compilation=await compileRuntime(dir,options).catch(error=>{throw new Error(error.stdout||error.stderr||error.message)})
const cases=JSON.parse(await readFile(join(dir,'cases.json'),'utf8'))
const controls=[{id:'compiled-command-boundary',kind:'boundary',cancellation:'throw',providers:{},turns:[]},
 {id:'compiled-wire-parser',kind:'parser',providers:{},lines:parserCases().map(c=>c.source)},
 {id:'compiled-wire-validation',kind:'wire-control',providers:{1:'hold'},turns:[{label:'invalid',lines:['{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"session_command","arguments":{"op":"spawn","label":"invalid","extra":true}}}']}]}]
await writeFile(join(dir,'runtime-cases.json'),JSON.stringify([...cases,...controls],null,2))
const stdout=execFileSync('dotnet',[compilation.assembly,join(dir,'runtime-cases.json')],options)
await writeFile(join(dir,'runtime-raw.jsonl'),stdout)
const rows=stdout.trim().split(/\r?\n/).map(x=>JSON.parse(x)),proof=[]
assert.equal(rows.length,cases.length+6)
const acceptsCancellation=row=>{
 assert.equal(row.response.ok,true,'child cancellation must settle the owned scope normally')
 assert.equal(row.response.result.actual.isOk,false)
 assert.equal(row.response.result.actual.value,'command-cancelled')
 assert.equal(row.Opened,1);assert.equal(row.Closed,1)
 assert.ok(!row.response.result.calls.some(x=>x.operation==='workspaceCreate'||(x.operation==='gate'&&arg(x,'label')==='enter:cancelled')))
}
for(const row of rows){
 if(row.id==='compiled-wire-parser'){
  assert.equal(row.response.ok,true)
  let rejection;try{assert.deepEqual(row.response.result.items,parserCases().map(c=>c.expected))}catch(error){rejection=error.message.split('\n')[0]}
  assert.equal(!!rejection,row.wrongBoundary,'same parser/ID acceptance rejects wrong correlation')
  assert.deepEqual(row.response.result.calls,[]);assert.equal(row.Opened,0);assert.equal(row.Closed,0)
  proof.push({id:row.id,wrongId:row.wrongBoundary,rejected:!!rejection,count:parserCases().length})
 }else if(row.id==='compiled-wire-validation'){
  assert.equal(row.response.ok,true);assert.equal(row.Opened,1);assert.equal(row.Closed,1)
  const calls=row.response.result.calls
  let rejection;try{assert.ok(!calls.some(x=>x.operation==='workspaceCreate'||(x.operation==='gate'&&arg(x,'label')==='request-start:invalid')),'invalid input must not dispatch/register or mutate')}catch(error){rejection=error.message}
  assert.equal(!!rejection,row.wrongBoundary,'same no-dispatch acceptance rejects validation after dispatch')
  assert.deepEqual(calls.filter(x=>x.operation==='reply'&&arg(x,'label')==='@wire').map(x=>arg(x,'record')),['{"jsonrpc":"2.0","id":3,"error":{"code":-32602,"message":"Invalid params"}}'])
  proof.push({id:row.id,wrongBoundary:row.wrongBoundary,rejected:!!rejection,workspaceEffects:calls.filter(x=>x.operation==='workspaceCreate').length})
 }else if(row.id==='compiled-command-boundary'){
  let rejection;try{acceptsCancellation(row)}catch(error){rejection=error.message}
  assert.equal(!rejection,!row.wrongBoundary,'same acceptance rejects compiled wait-only catch')
  if(row.wrongBoundary){
   assert.equal(row.identityPreserved,true);assert.equal(row.throwingStackPreserved,true)
   assert.ok(row.response.error.calls.some(x=>x.operation==='reply'&&arg(x,'label')==='@join-only'),'wrong boundary catch actually executed')
  }
  proof.push({id:row.id,wrongBoundary:row.wrongBoundary,rejected:!!rejection,reason:rejection??null})
 }else{
  const c=cases.find(x=>x.id===row.id)
  assert.equal(row.Opened,1);assert.equal(row.Closed,1)
  if(c.kind==='session'){proof.push(acceptance(c,row));assert.equal(row.MaxActive,2,'two providers overlap')}
  else{
   assert.equal(row.response.ok,false,'raw worker/cleanup/release faults escape')
   assert.equal(row.identityPreserved,true,'original fault object and precedence retained')
   assert.equal(row.throwingStackPreserved,true,'original throwing frame retained')
   const calls=row.response.error.calls
   assert.equal(calls.filter(x=>x.operation==='close').length,1)
   assert.ok(!calls.some(x=>x.operation==='reply'&&['terminal','@wire-terminal'].includes(arg(x,'label'))),'failed run publishes no success terminal report')
   if(c.kind==='cleanup-fault'){
    assert.ok(calls.some(x=>x.operation==='reply'&&arg(x,'label')==='cancelled:outcome'))
    assert.equal(calls.filter(x=>x.operation==='gate'&&arg(x,'label')==='command-cleanup:cancelled').length,1)
   }
   proof.push({id:c.id,fault:row.response.error.type,identityPreserved:true,throwingStackPreserved:true,releaseCount:1})
  }
 }
 console.log(`PASS: compiled ${row.id}${row.wrongBoundary?' (wrong-boundary rejected)':''}`)
}
assert.equal(hash(await readFile(join(dir,'Generated.cs'))),original)
await writeFile(join(dir,'runtime-evidence.json'),JSON.stringify({proof,sourceUnchanged:true,generatedSha256:original,assemblySha256:hash(await readFile(compilation.assembly)),compilation:compilation.evidence},null,2)+'\n')
console.log('PASS: unedited emitted C#, offline compilation, per-request effects, joins and once-only release')
