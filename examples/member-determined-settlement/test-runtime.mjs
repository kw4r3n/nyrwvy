import assert from 'node:assert/strict'
import {mkdtemp,copyFile,readFile,writeFile,rm} from 'node:fs/promises'
import {join,resolve} from 'node:path'
import {tmpdir} from 'node:os'
import {createHash} from 'node:crypto'
import {execFileSync} from 'node:child_process'
import {compileRuntime} from '../dynamic-worker-collector/compile-runtime.mjs'
import {acceptance,normalize} from './cases.mjs'
const input=resolve(process.argv[2]),hash=x=>createHash('sha256').update(x).digest('hex')
const graph=JSON.parse(await readFile(join(input,'controller.json'),'utf8')),manifest=JSON.parse(await readFile(join(input,'symbols.json'),'utf8'))
const authoring=JSON.parse(await readFile(join(input,'authoring.json'),'utf8'))
const source=await readFile(join(input,'Generated.cs'))
assert.equal(hash(source),authoring.generatedSha256)
const symbol=(kind,id)=>manifest[kind].find(x=>x.id===id).name
const find=(items,name)=>items.find(x=>x.displayName===name)
const host=find(graph.capabilities,'Host'),run=find(graph.functions,'ownerProbe'),caller=find(graph.functions,'callerCancellationRun'),request=find(graph.types,'Request')
const csType=t=>t.kind==='primitive'?({string:'NyStr',unit:'NyUnit',int64:'long',bool:'bool'})[t.primitive]:`${({result:'NyResult',list:'NyList'})[t.generic]}<${t.arguments.map(csType).join(',')}>`
const effectNames={open:'Open',close:'Close',workspaceCreate:'WorkspaceCreate',workspaceDiscard:'WorkspaceDiscard',provider:'Provider',memberClose:'MemberClose',snapshot:'Snapshot',checks:'Checks',gate:'Gate',reply:'Reply',step:'Step'}
const variables={HOST:symbol('capabilities',host.id),RUN:symbol('functions',run.id),RUN_HOST:symbol('parameters',find(run.parameters,'host').id),RUN_REQUESTS:symbol('parameters',find(run.parameters,'requests').id),CALLER_RUN:symbol('functions',caller.id),CALLER_HOST:symbol('parameters',find(caller.parameters,'host').id),REQUEST:symbol('types',request.id),
 METHODS:host.operations.map(o=>`public ${csType(o.resultType)} ${symbol('capabilityOperations',o.id)}(${o.parameters.map(p=>`${csType(p.type)} ${p.displayName==='base'?'basis':p.displayName}`).join(',')}) => ${effectNames[o.displayName]}(${o.parameters.map(p=>p.displayName==='base'?'basis':p.displayName).join(',')});`).join('\n'),
 REQUESTARGS:request.fields.map(f=>`${symbol('fields',f.id)}: ${f.type.primitive==='string'?`(NyStr)r.GetProperty("${f.displayName}").GetString()!`:`r.GetProperty("${f.displayName}").GetInt64()`}`).join(',')}
const program=(await readFile(new URL('./runtime.template.cs',import.meta.url),'utf8')).replace(/@@([A-Z_]+)@@/g,(_,k)=>{assert.ok(variables[k],k);return variables[k]})
const scratch=await mkdtemp(join(tmpdir(),'member-runtime-'))
try {
 await copyFile(join(input,'Generated.cs'),join(scratch,'Generated.cs'))
 await writeFile(join(scratch,'Program.cs'),program)
 const options={cwd:scratch,encoding:'utf8',env:{...process.env,DOTNET_NOLOGO:'1',DOTNET_CLI_TELEMETRY_OPTOUT:'1'},timeout:90000}
 const {assembly,evidence:compilation}=await compileRuntime(scratch,options)
 const stdout=execFileSync('dotnet',[assembly,join(input,'cases.json')],options)
 await writeFile(join(input,'runtime-raw.json'),stdout)
 const results=stdout.trim().split(/\r?\n/).map(x=>JSON.parse(x)),cases=JSON.parse(await readFile(join(input,'cases.json'),'utf8'))
 assert.equal(results.length,cases.length*2)
 for(const row of results){
  const c=cases.find(x=>x.id===row.id)
  assert.equal(row.response.ok,c.snapshotFault!=='raw'&&!c.releaseFault,JSON.stringify(row.response.error))
  const calls=row.response.ok?row.response.result.calls:row.response.error.calls
  const release=calls.findIndex(x=>x.operation==='close')
  assert.ok(calls.every((x,i)=>!['workspaceCreate','workspaceDiscard','step','provider','memberClose','snapshot','checks'].includes(x.operation)||i<release),'all worker/mutation effects precede release')
  assert.ok(row.MaxActive<=2)
  row.checks=normalize(c,row.response);if(!authoring.baseline)acceptance(c,row.checks)
  assert.equal(row.Opened,1);assert.equal(row.Closed,1)
  console.log(`PASS: real runtime ${row.id}`)
 }
 assert.equal(hash(await readFile(join(scratch,'Generated.cs'))),authoring.generatedSha256)
 assert.equal(hash(await readFile(join(input,'Generated.cs'))),authoring.generatedSha256)
 await writeFile(join(input,'runtime-evidence.json'),JSON.stringify({generatedSha256:authoring.generatedSha256,sourceUnchanged:true,compilation,results},null,2))
 await writeFile(join(input,'Runtime.Program.cs'),program)
 console.log('PASS: unedited generated C#, offline compilation, scope joins and provider closure')
}finally{await rm(scratch,{recursive:true,force:true})}
